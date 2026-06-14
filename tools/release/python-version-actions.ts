import { ProjectGraph, ProjectGraphDependency, Tree } from '@nx/devkit';
import { VersionActions } from 'nx/release';

/**
 * Custom version actions for Python projects.
 * Uses regex replacement to update pyproject.toml without reformatting.
 */
export default class PythonVersionActions extends VersionActions {
  validManifestFilenames = null;

  async init(_tree: Tree): Promise<void> {
    return;
  }

  async readDependencies(
    _tree: Tree,
    _projectGraph: ProjectGraph
  ): Promise<ProjectGraphDependency[]> {
    return [];
  }

  async readCurrentVersionFromSourceManifest(tree: Tree): Promise<{
    currentVersion: string;
    manifestPath: string;
  }> {
    const projectRoot = this.projectGraphNode.data.root;
    const manifestPath = `${projectRoot}/pyproject.toml`;
    const content = tree.read(manifestPath, 'utf-8');

    if (!content) {
      throw new Error(`pyproject.toml not found at ${manifestPath}`);
    }

    const match = content.match(/^version\s*=\s*"([^"]+)"/m);
    if (!match) {
      throw new Error(`No version field found in ${manifestPath}`);
    }

    return { currentVersion: match[1], manifestPath };
  }

  async readCurrentVersionFromRegistry(
    _tree: Tree,
    _currentVersionResolverMetadata?: Record<string, unknown>
  ): Promise<{
    currentVersion: string;
    logText: string;
  }> {
    throw new Error(
      'Registry version resolution not supported for Python projects'
    );
  }

  async readCurrentVersionOfDependency(
    _tree: Tree,
    _projectGraph: ProjectGraph,
    _dependencyProjectName: string
  ): Promise<{
    currentVersion: string | null;
    dependencyCollection: string | null;
  }> {
    return { currentVersion: null, dependencyCollection: null };
  }

  async updateProjectVersion(
    tree: Tree,
    newVersion: string
  ): Promise<string[]> {
    const { manifestPath } =
      await this.readCurrentVersionFromSourceManifest(tree);
    const content = tree.read(manifestPath, 'utf-8');

    if (!content) {
      throw new Error(`pyproject.toml not found at ${manifestPath}`);
    }

    const versionRegex = /^(version\s*=\s*")([^"]+)(")/m;
    if (!content.match(versionRegex)) {
      throw new Error(`No version field found in ${manifestPath}`);
    }

    const updatedContent = content.replace(
      versionRegex,
      `$1${newVersion}$3`
    );
    tree.write(manifestPath, updatedContent);

    return [`Updated version in ${manifestPath} to ${newVersion}`];
  }

  async updateProjectDependencies(
    _tree: Tree,
    _projectGraph: ProjectGraph,
    _dependenciesToUpdate: Record<string, string>
  ): Promise<string[]> {
    return [];
  }
}
