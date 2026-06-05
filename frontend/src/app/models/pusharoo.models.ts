export interface Project {
  id: string;
  name: string;
  description?: string | null;
  createdAt: string;
}

export interface ArtifactSummary {
  methodCount: number;
  eventCount: number;
  permissionCount: number;
  supportedStandards: string[];
}

export interface NeoContractManifest {
  name: string;
  groups: unknown[];
  features: Record<string, unknown>;
  supportedstandards: string[];
  abi: NeoAbi;
  permissions: NeoPermission[];
  trusts: unknown[];
  extra: Record<string, unknown>;
}

export interface NeoAbi {
  methods: NeoMethod[];
  events: NeoEvent[];
}

export interface NeoMethod {
  name: string;
  parameters: NeoParameter[];
  returntype?: string;
  returnType?: string;
  offset: number;
  safe: boolean;
}

export interface NeoEvent {
  name: string;
  parameters: NeoParameter[];
}

export interface NeoParameter {
  name: string;
  type: string;
}

export interface NeoPermission {
  contract: unknown;
  methods: unknown;
}

export interface Artifact {
  id: string;
  projectId: string;
  version: string;
  notes?: string | null;
  contractName: string;
  nefFileName: string;
  nefSize: number;
  manifest: NeoContractManifest;
  summary: ArtifactSummary;
  warnings: string[];
  createdAt: string;
}

export interface ArtifactComparison {
  addedMethods: string[];
  removedMethods: string[];
  changedMethods: ChangedMethod[];
  addedEvents: string[];
  permissionChanges: string[];
}

export interface ChangedMethod {
  name: string;
  changes: string[];
}

export interface ProjectCardViewModel {
  project: Project;
  artifacts: Artifact[];
  latestArtifact: Artifact | null;
  deployed: boolean;
}

export interface ProjectOverviewViewModel extends ProjectCardViewModel {
  latestArtifact: Artifact | null;
}
