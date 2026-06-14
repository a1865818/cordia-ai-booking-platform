#!/usr/bin/env node

import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import path from "node:path";
import { spawn } from "node:child_process";

function usage() {
  console.error("Usage: node export_openapi.mjs <api-csproj-path> <output-json-path> [preferred-configuration]");
  process.exit(1);
}

async function getFreePort() {
  return await new Promise((resolve, reject) => {
    const server = net.createServer();
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      if (!address || typeof address === "string") {
        server.close(() => reject(new Error("Unable to determine free port.")));
        return;
      }

      const { port } = address;
      server.close((error) => {
        if (error) {
          reject(error);
          return;
        }

        resolve(port);
      });
    });
  });
}

function requestJson(url) {
  return new Promise((resolve, reject) => {
    const request = http.get(url, (response) => {
      const chunks = [];
      response.on("data", (chunk) => chunks.push(chunk));
      response.on("end", () => {
        const body = Buffer.concat(chunks).toString("utf8");
        if (response.statusCode !== 200) {
          reject(new Error(`Received ${response.statusCode} from ${url}\n${body}`));
          return;
        }

        try {
          resolve(JSON.parse(body));
        } catch (error) {
          reject(new Error(`Invalid JSON received from ${url}: ${error.message}`));
        }
      });
    });

    request.on("error", reject);
  });
}

function stableSort(value) {
  if (Array.isArray(value)) {
    return value.map(stableSort);
  }

  if (value && typeof value === "object") {
    return Object.keys(value)
      .sort((left, right) => left.localeCompare(right, "en"))
      .reduce((result, key) => {
        result[key] = stableSort(value[key]);
        return result;
      }, {});
  }

  // Normalize \r\n → \n in string values so output is identical on Windows and Linux.
  // dotnet embeds platform-dependent line endings in XML doc comments.
  if (typeof value === "string") {
    return value.replaceAll("\r\n", "\n");
  }

  return value;
}

async function waitForSwagger(url, timeoutMs) {
  const startedAt = Date.now();
  let lastError = null;

  while (Date.now() - startedAt < timeoutMs) {
    try {
      return await requestJson(url);
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
  }

  throw new Error(`Timed out waiting for ${url}${lastError ? `\nLast error: ${lastError.message}` : ""}`);
}

async function terminate(child) {
  if (child.exitCode !== null) {
    return;
  }

  child.kill("SIGTERM");

  await new Promise((resolve) => {
    const timer = setTimeout(() => {
      if (child.exitCode === null) {
        child.kill("SIGKILL");
      }
      resolve();
    }, 5_000);

    child.once("exit", () => {
      clearTimeout(timer);
      resolve();
    });
  });
}

async function sleep(ms) {
  await new Promise((resolve) => setTimeout(resolve, ms));
}

async function acquireLock(lockPath, timeoutMs = 60_000) {
  const startedAt = Date.now();

  while (Date.now() - startedAt < timeoutMs) {
    try {
      fs.mkdirSync(lockPath);
      fs.writeFileSync(
        path.join(lockPath, "owner.json"),
        JSON.stringify({ pid: process.pid, acquiredAt: new Date().toISOString() }, null, 2),
      );
      return;
    } catch (error) {
      if (error.code !== "EEXIST") {
        throw error;
      }

      await sleep(250);
    }
  }

  throw new Error(`Timed out waiting for export lock: ${lockPath}`);
}

function releaseLock(lockPath) {
  fs.rmSync(lockPath, { recursive: true, force: true });
}

async function waitForLaunchArtifacts(dllPath, timeoutMs = 30_000) {
  const startedAt = Date.now();
  const runtimeConfigPath = dllPath.replace(/\.dll$/u, ".runtimeconfig.json");
  const depsPath = dllPath.replace(/\.dll$/u, ".deps.json");
  const requiredPaths = [dllPath, runtimeConfigPath, depsPath];

  while (Date.now() - startedAt < timeoutMs) {
    if (requiredPaths.every((filePath) => fs.existsSync(filePath))) {
      return;
    }

    await sleep(250);
  }

  throw new Error(
    `Timed out waiting for launch artifacts: ${requiredPaths.join(", ")}`,
  );
}

function getDllPath(projectPath, preferredConfiguration = "Debug") {
  const projectDir = path.dirname(projectPath);
  const assemblyName = path.basename(projectPath, path.extname(projectPath));
  const binDir = path.join(projectDir, "bin");

  if (!fs.existsSync(binDir)) {
    throw new Error(`Build output directory not found: ${binDir}`);
  }

  const configurations = fs.readdirSync(binDir, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name);

  const orderedConfigurations = [
    preferredConfiguration,
    ...configurations.filter((name) => name !== preferredConfiguration),
  ];

  for (const configuration of orderedConfigurations) {
    const configDir = path.join(binDir, configuration);
    if (!fs.existsSync(configDir)) continue;
    const tfms = fs.readdirSync(configDir, { withFileTypes: true })
      .filter((entry) => entry.isDirectory())
      .map((entry) => entry.name);
    for (const tfm of tfms) {
      const candidate = path.join(binDir, configuration, tfm, `${assemblyName}.dll`);
      if (fs.existsSync(candidate)) {
        return candidate;
      }
    }
  }

  throw new Error(
    `Unable to locate ${assemblyName}.dll under ${binDir}. Tried configurations: ${orderedConfigurations.join(", ")}`,
  );
}

const [projectPathArg, outputPathArg, preferredConfigurationArg] = process.argv.slice(2);

if (!projectPathArg || !outputPathArg) {
  usage();
}

const projectPath = path.resolve(process.cwd(), projectPathArg);
const outputPath = path.resolve(process.cwd(), outputPathArg);
const preferredConfiguration = preferredConfigurationArg || "Debug";

if (!fs.existsSync(projectPath)) {
  throw new Error(`API project not found: ${projectPath}`);
}

const dllPath = getDllPath(projectPath, preferredConfiguration);

const outputDir = path.dirname(outputPath);
fs.mkdirSync(outputDir, { recursive: true });
const lockPath = path.join(outputDir, ".export-openapi.lock");

await acquireLock(lockPath);

try {
  await waitForLaunchArtifacts(dllPath);

  const port = await getFreePort();
  const baseUrl = `http://127.0.0.1:${port}`;
  const swaggerUrl = `${baseUrl}/swagger/v1/swagger.json`;
  const stderr = [];

  console.log(`Starting API for OpenAPI export on ${baseUrl}`);
  console.log(`Using assembly: ${dllPath}`);

  const child = spawn("dotnet", [dllPath], {
    cwd: path.dirname(dllPath),
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: "Development",
      ASPNETCORE_URLS: baseUrl,
      ConnectionStrings__MAgeDBConnection: "Host=127.0.0.1;Port=5432;Database=openapi_export;Username=openapi;Password=openapi",
      DatabaseBootstrap__Enabled: "false",
      OpenApiExport__Enabled: "true",
    },
    stdio: ["ignore", "pipe", "pipe"],
  });

  child.stdout.on("data", (chunk) => process.stdout.write(chunk));
  child.stderr.on("data", (chunk) => {
    const text = chunk.toString("utf8");
    stderr.push(text);
    process.stderr.write(text);
  });

  try {
    const document = await waitForSwagger(swaggerUrl, 30_000);
    const stableDocument = stableSort(document);
    const tempOutputPath = path.join(
      outputDir,
      `.v1.${process.pid}.${Date.now()}.${Math.random().toString(16).slice(2)}.tmp`,
    );

    fs.writeFileSync(tempOutputPath, `${JSON.stringify(stableDocument, null, 2)}\n`);
    fs.renameSync(tempOutputPath, outputPath);
    console.log(`OpenAPI document exported to ${outputPath}`);
  } catch (error) {
    const diagnostic = stderr.length > 0 ? `\nAPI stderr:\n${stderr.join("")}` : "";
    throw new Error(`${error.message}${diagnostic}`);
  } finally {
    await terminate(child);
  }
} finally {
  releaseLock(lockPath);
}
