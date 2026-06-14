import { ProjectGraph, ProjectGraphDependency, Tree } from '@nx/devkit';
import { VersionActions } from 'nx/release';
import { XMLParser } from 'fast-xml-parser';

/**
 * Custom version actions for .NET projects.
 * Updates the <Version> property in .csproj files.
 */
export default class DotnetVersionActions extends VersionActions {
  // Set to null so we do not rely on NX's literal filename matching.
  validManifestFilenames = null;

  // No-op: we do our own manifest discovery and do not rely on base init behavior.
  async init(_tree: Tree): Promise<void> {
    return;
  }

  async readDependencies(
    tree: Tree,
    projectGraph: ProjectGraph
  ): Promise<ProjectGraphDependency[]> {
    return [];
  }

  async readCurrentVersionFromSourceManifest(tree: Tree): Promise<{
    currentVersion: string;
    manifestPath: string;
  }> {
    const projectRoot = this.projectGraphNode.data.root;
    const matches = this.findCsprojWithVersion(tree, projectRoot);
    if (matches.length === 0) {
      throw new Error(
        `No .csproj file with <Version> property found under ${projectRoot}`
      );
    }
    if (matches.length > 1) {
      throw new Error(
        `Multiple .csproj files with <Version> property found under ${projectRoot}:\n- ${matches.join(
          '\n- '
        )}`
      );
    }

    const manifestPath = matches[0];
    const content = tree.read(manifestPath, 'utf-8');
    const version = this.extractVersionFromCsproj(content);

    return { currentVersion: version, manifestPath };
  }

  /**
   * Recursively searches for a .csproj file containing a <Version> property.
   * Searches the project root and up to 3 levels of subdirectories.
   */
  private findCsprojWithVersion(
    tree: Tree,
    dir: string,
    depth: number = 0,
    matches: string[] = []
  ): string[] {
    if (depth > 3) return matches;

    let children: string[];
    try {
      children = tree.children(dir);
    } catch {
      return matches;
    }

    // Check .csproj files in this directory first
    for (const child of children) {
      if (child.endsWith('.csproj')) {
        const fullPath = `${dir}/${child}`;
        const content = tree.read(fullPath, 'utf-8');
        if (content && /<Version>/.test(content)) {
          matches.push(fullPath);
          if (matches.length > 1) return matches;
        }
      }
    }

    // Recurse into subdirectories
    for (const child of children) {
      const childPath = `${dir}/${child}`;
      if (tree.isFile(childPath)) continue;
      this.findCsprojWithVersion(tree, childPath, depth + 1, matches);
      if (matches.length > 1) return matches;
    }

    return matches;
  }

  async readCurrentVersionFromRegistry(
    _tree: Tree,
    _currentVersionResolverMetadata?: Record<string, unknown>
  ): Promise<{
    currentVersion: string;
    logText: string;
  }> {
    throw new Error('Registry version resolution not supported for .NET projects');
  }

  async readCurrentVersionOfDependency(
    tree: Tree,
    projectGraph: ProjectGraph,
    dependencyProjectName: string
  ): Promise<{
    currentVersion: string | null;
    dependencyCollection: string | null;
  }> {
    return { currentVersion: null, dependencyCollection: null };
  }

  async updateProjectVersion(tree: Tree, newVersion: string): Promise<string[]> {
    const { manifestPath } = await this.readCurrentVersionFromSourceManifest(tree);
    const content = tree.read(manifestPath, 'utf-8');
    const updatedContent = this.updateVersionInCsproj(content, newVersion);

    tree.write(manifestPath, updatedContent);

    return [`Updated <Version> in ${manifestPath} to ${newVersion}`];
  }

  async updateProjectDependencies(
    tree: Tree,
    projectGraph: ProjectGraph,
    dependenciesToUpdate: Record<string, string>
  ): Promise<string[]> {
    return [];
  }

  private extractVersionFromCsproj(content: string): string {
    const parser = new XMLParser({
      ignoreAttributes: false,
      preserveOrder: false,
    });

    const xml = parser.parse(content);
    const propertyGroups = Array.isArray(xml.Project?.PropertyGroup)
      ? xml.Project.PropertyGroup
      : [xml.Project?.PropertyGroup].filter(Boolean);

    for (const group of propertyGroups) {
      const versionValue = group?.Version;
      if (typeof versionValue === 'string' && versionValue.trim().length > 0) {
        return versionValue;
      }
      if (Array.isArray(versionValue) && versionValue.length > 0) {
        return String(versionValue[0]);
      }
      if (
        versionValue &&
        typeof versionValue === 'object' &&
        '#text' in versionValue
      ) {
        return String((versionValue as { ['#text']: string })['#text']);
      }
    }

    throw new Error('No <Version> property found in .csproj');
  }

  private updateVersionInCsproj(content: string, newVersion: string): string {
    const versionRegex = /<Version>.*?<\/Version>/;
    const match = content.match(versionRegex);

    if (!match) {
      throw new Error('No <Version> property found in .csproj');
    }

    return content.replace(versionRegex, `<Version>${newVersion}</Version>`);
  }
}
