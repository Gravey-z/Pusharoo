export interface Project {
  id: string;
  name: string;
  description?: string | null;
  createdByWalletAddress?: string | null;
  creatorNetwork?: string | null;
  createdAt: string;
}

export interface WalletActionSignature {
  address: string;
  scriptHash: string;
  network: string;
  provider: string;
  origin: string;
  issuedAtUtc: string;
  nonce: string;
  message: string;
  publicKey: string;
  data: string;
  salt?: string | null;
  messageHex?: string | null;
}

export type ProjectCreationSignature = WalletActionSignature;

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

export interface Deployment {
  id: string;
  projectId: string;
  artifactId: string;
  version: string;
  network: string;
  contractHash?: string | null;
  transactionId?: string | null;
  deployedBy: string;
  notes?: string | null;
  createdAt: string;
}

export interface CreateDeploymentRequest {
  artifactId: string;
  network: string;
  contractHash?: string | null;
  transactionId?: string | null;
  deployedBy: string;
  notes?: string | null;
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

export interface WebhookSubscription {
  id: string;
  projectId?: string | null;
  name: string;
  contractHash: string;
  eventName?: string | null;
  webhookUrl: string;
  headers: Record<string, string>;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateWebhookSubscriptionRequest {
  name: string;
  contractHash: string;
  eventName?: string | null;
  webhookUrl: string;
  projectId?: string | null;
  secret?: string | null;
  headers?: Record<string, string>;
  isEnabled: boolean;
}

export interface WebhookDelivery {
  id: string;
  subscriptionId: string;
  eventId: string;
  webhookUrl: string;
  statusCode?: number | null;
  succeeded: boolean;
  error?: string | null;
  deliveredAt: string;
}

export interface ProjectCardViewModel {
  project: Project;
  artifacts: Artifact[];
  latestArtifact: Artifact | null;
  deployments: Deployment[];
  latestDeployment: Deployment | null;
  deployed: boolean;
}

export interface ProjectOverviewViewModel extends ProjectCardViewModel {
  latestArtifact: Artifact | null;
}
