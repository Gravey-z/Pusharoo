import {
  Artifact,
  Deployment,
  Project,
  ProjectCardViewModel,
  RelayPaymentHistory,
  RelayUsage,
  WebhookDelivery,
  WebhookSubscription
} from '../models/pusharoo.models';

export const DEMO_WALLET_ADDRESS = 'NDemoPusharooWallet0000000000000000';
export const DEMO_WALLET_SCRIPT_HASH = '0123456789abcdef0123456789abcdef01234567';

export const demoProject: Project = {
  id: 'demo-vaulten',
  name: 'Vaulten Contract',
  description: 'NFT vault release workspace with production-style deployment history.',
  createdByWalletAddress: DEMO_WALLET_ADDRESS,
  creatorNetwork: 'neo3:testnet',
  createdAt: '2026-05-28T00:00:00Z'
};

export const pusharooProject: Project = {
  id: 'demo-pusharoo',
  name: 'Pusharoo',
  description: 'Deployment and artifact registry used to demonstrate the release workflow.',
  createdByWalletAddress: DEMO_WALLET_ADDRESS,
  creatorNetwork: 'neo3:testnet',
  createdAt: '2026-05-28T00:00:00Z'
};

export const brainTeseeProject: Project = {
  id: 'demo-braintesee',
  name: 'BrainTesee',
  description: 'Puzzle contract experiment with a failed release ready to inspect.',
  createdByWalletAddress: DEMO_WALLET_ADDRESS,
  creatorNetwork: 'neo3:testnet',
  createdAt: '2026-05-28T00:00:00Z'
};

export const demoArtifacts: Artifact[] = [
  {
    id: 'demo-vaulten-012',
    projectId: demoProject.id,
    version: '0.1.2',
    notes: 'Latest testnet build',
    contractName: 'Vaulten',
    nefFileName: 'Vaulten.nef',
    nefSize: 12345,
    createdAt: '2026-05-28T00:00:00Z',
    warnings: [],
    summary: {
      methodCount: 12,
      eventCount: 4,
      permissionCount: 2,
      supportedStandards: ['NEP-11']
    },
    manifest: {
      name: 'Vaulten',
      groups: [],
      features: {},
      supportedstandards: ['NEP-11'],
      abi: {
        methods: [
          { name: 'symbol', parameters: [], returntype: 'String', offset: 0, safe: true },
          {
            name: 'balanceOf',
            parameters: [{ name: 'account', type: 'Hash160' }],
            returntype: 'Integer',
            offset: 8,
            safe: true
          },
          {
            name: 'transfer',
            parameters: [
              { name: 'from', type: 'Hash160' },
              { name: 'to', type: 'Hash160' },
              { name: 'amount', type: 'Integer' },
              { name: 'data', type: 'Any' }
            ],
            returntype: 'Boolean',
            offset: 16,
            safe: false
          }
        ],
        events: [
          {
            name: 'Transfer',
            parameters: [
              { name: 'from', type: 'Hash160' },
              { name: 'to', type: 'Hash160' },
              { name: 'amount', type: 'Integer' }
            ]
          },
          {
            name: 'OnSolved',
            parameters: [
              { name: 'player', type: 'Hash160' },
              { name: 'puzzleId', type: 'ByteString' }
            ]
          }
        ]
      },
      permissions: [
        { contract: '*', methods: ['transfer'] },
        { contract: '*', methods: ['onNEP11Payment'] }
      ],
      trusts: [],
      extra: {}
    }
  },
  {
    id: 'demo-vaulten-011',
    projectId: demoProject.id,
    version: '0.1.1',
    notes: 'Contract ABI updates',
    contractName: 'Vaulten',
    nefFileName: 'Vaulten.nef',
    nefSize: 11920,
    createdAt: '2026-05-27T00:00:00Z',
    warnings: [],
    summary: {
      methodCount: 10,
      eventCount: 3,
      permissionCount: 2,
      supportedStandards: ['NEP-11']
    },
    manifest: {
      name: 'Vaulten',
      groups: [],
      features: {},
      supportedstandards: ['NEP-11'],
      abi: { methods: [], events: [] },
      permissions: [],
      trusts: [],
      extra: {}
    }
  },
  {
    id: 'demo-vaulten-010',
    projectId: demoProject.id,
    version: '0.1.0',
    notes: 'Initial testnet build',
    contractName: 'Vaulten',
    nefFileName: 'Vaulten.nef',
    nefSize: 11284,
    createdAt: '2026-05-26T00:00:00Z',
    warnings: [],
    summary: {
      methodCount: 8,
      eventCount: 2,
      permissionCount: 1,
      supportedStandards: ['NEP-11']
    },
    manifest: {
      name: 'Vaulten',
      groups: [],
      features: {},
      supportedstandards: ['NEP-11'],
      abi: { methods: [], events: [] },
      permissions: [],
      trusts: [],
      extra: {}
    }
  },
  {
    id: 'demo-pusharoo-020',
    projectId: pusharooProject.id,
    version: '0.2.0',
    notes: 'Artifact index prototype',
    contractName: 'Pusharoo',
    nefFileName: 'Pusharoo.nef',
    nefSize: 18420,
    createdAt: '2026-05-28T00:00:00Z',
    warnings: [],
    summary: {
      methodCount: 9,
      eventCount: 3,
      permissionCount: 1,
      supportedStandards: ['NEP-17']
    },
    manifest: {
      name: 'Pusharoo',
      groups: [],
      features: {},
      supportedstandards: ['NEP-17'],
      abi: {
        methods: [
          { name: 'registerProject', parameters: [{ name: 'name', type: 'String' }], returntype: 'Boolean', offset: 0, safe: false },
          { name: 'artifactCount', parameters: [{ name: 'projectId', type: 'ByteString' }], returntype: 'Integer', offset: 12, safe: true },
          { name: 'latestArtifact', parameters: [{ name: 'projectId', type: 'ByteString' }], returntype: 'ByteString', offset: 24, safe: true }
        ],
        events: [
          { name: 'ProjectRegistered', parameters: [{ name: 'projectId', type: 'ByteString' }, { name: 'name', type: 'String' }] },
          { name: 'ArtifactUploaded', parameters: [{ name: 'projectId', type: 'ByteString' }, { name: 'version', type: 'String' }] },
          { name: 'DeploymentLinked', parameters: [{ name: 'artifactId', type: 'ByteString' }] }
        ]
      },
      permissions: [{ contract: '*', methods: ['transfer'] }],
      trusts: [],
      extra: {}
    }
  },
  {
    id: 'demo-braintesee-003',
    projectId: brainTeseeProject.id,
    version: '0.0.3',
    notes: 'Puzzle scoring pass',
    contractName: 'BrainTesee',
    nefFileName: 'BrainTesee.nef',
    nefSize: 15304,
    createdAt: '2026-05-28T00:00:00Z',
    warnings: [],
    summary: {
      methodCount: 7,
      eventCount: 2,
      permissionCount: 2,
      supportedStandards: []
    },
    manifest: {
      name: 'BrainTesee',
      groups: [],
      features: {},
      supportedstandards: [],
      abi: {
        methods: [
          { name: 'createPuzzle', parameters: [{ name: 'seed', type: 'ByteString' }], returntype: 'ByteString', offset: 0, safe: false },
          { name: 'solve', parameters: [{ name: 'puzzleId', type: 'ByteString' }, { name: 'answer', type: 'ByteString' }], returntype: 'Boolean', offset: 10, safe: false },
          { name: 'scoreOf', parameters: [{ name: 'player', type: 'Hash160' }], returntype: 'Integer', offset: 22, safe: true }
        ],
        events: [
          { name: 'PuzzleCreated', parameters: [{ name: 'puzzleId', type: 'ByteString' }] },
          { name: 'OnSolved', parameters: [{ name: 'player', type: 'Hash160' }, { name: 'puzzleId', type: 'ByteString' }] }
        ]
      },
      permissions: [
        { contract: '*', methods: ['verify'] },
        { contract: '*', methods: ['transfer'] }
      ],
      trusts: [],
      extra: {}
    }
  }
];

export const demoProjectCards: ProjectCardViewModel[] = [
  {
    project: demoProject,
    artifacts: demoArtifacts.filter((artifact) => artifact.projectId === demoProject.id),
    latestArtifact: demoArtifacts.find((artifact) => artifact.projectId === demoProject.id) ?? null,
    deployments: [],
    latestDeployment: null,
    deployed: false
  },
  {
    project: pusharooProject,
    artifacts: demoArtifacts.filter((artifact) => artifact.projectId === pusharooProject.id),
    latestArtifact: demoArtifacts.find((artifact) => artifact.projectId === pusharooProject.id) ?? null,
    deployments: [],
    latestDeployment: null,
    deployed: false
  },
  {
    project: brainTeseeProject,
    artifacts: demoArtifacts.filter((artifact) => artifact.projectId === brainTeseeProject.id),
    latestArtifact: demoArtifacts.find((artifact) => artifact.projectId === brainTeseeProject.id) ?? null,
    deployments: [],
    latestDeployment: null,
    deployed: false
  }
];

export const demoProjects: Project[] = [demoProject, pusharooProject, brainTeseeProject];

export const demoDeployments: Deployment[] = [
  {
    id: 'demo-deployment-vaulten-testnet',
    projectId: demoProject.id,
    artifactId: 'demo-vaulten-012',
    version: '0.1.2',
    network: 'neo3:testnet',
    contractHash: '0x4f3d8ad12f96e86c10b04f9b8bf5cba936d44b67',
    transactionId: '0x9e72d72a34d983eeb65f32793402ad8709558d0bf010cda8f98edc8c67b8dc11',
    deployedBy: DEMO_WALLET_ADDRESS,
    notes: 'Validated TestNet release for the investor walkthrough.',
    createdAt: '2026-08-29T10:15:00Z',
    updatedAt: '2026-08-29T10:17:00Z',
    operation: 'update',
    status: 'confirmed'
  },
  {
    id: 'demo-deployment-vaulten-mainnet',
    projectId: demoProject.id,
    artifactId: 'demo-vaulten-011',
    version: '0.1.1',
    network: 'neo3:mainnet',
    contractHash: '0x759a982e3a0f46e9c6a3d4bdbf1d7990db2c2f18',
    transactionId: '0xd60b407735a618f75ed8f28de5b33c4894f6310f9e937af4f1258e256acf4902',
    deployedBy: DEMO_WALLET_ADDRESS,
    notes: 'Current MainNet release.',
    createdAt: '2026-08-17T14:20:00Z',
    updatedAt: '2026-08-17T14:23:00Z',
    operation: 'deploy',
    status: 'confirmed'
  },
  {
    id: 'demo-deployment-pusharoo-testnet',
    projectId: pusharooProject.id,
    artifactId: 'demo-pusharoo-020',
    version: '0.2.0',
    network: 'neo3:testnet',
    contractHash: '0x8c62f91636e7d9a5768f50f0a8cd87091d31ee9a',
    transactionId: '0x54853403c5e12a0cc3806c3962b21a5a95e75e17e6934f767cb63186db93d033',
    deployedBy: DEMO_WALLET_ADDRESS,
    notes: 'Release registry prototype.',
    createdAt: '2026-08-25T09:45:00Z',
    updatedAt: '2026-08-25T09:47:00Z',
    operation: 'deploy',
    status: 'confirmed'
  },
  {
    id: 'demo-deployment-braintesee-failed',
    projectId: brainTeseeProject.id,
    artifactId: 'demo-braintesee-003',
    version: '0.0.3',
    network: 'neo3:testnet',
    transactionId: '0x4ddab956d1a709301d19c74a35f67d7ab19ca43f385ab1fd639954776e96ae1c',
    deployedBy: DEMO_WALLET_ADDRESS,
    notes: 'Demonstrates a recoverable release failure.',
    createdAt: '2026-08-27T16:02:00Z',
    updatedAt: '2026-08-27T16:04:00Z',
    operation: 'deploy',
    status: 'failed',
    failureStage: 'confirmation',
    failureReason: 'The demo RPC provider timed out before the application log was available.'
  }
];

export const demoWebhookDeliveries: WebhookDelivery[] = [
  {
    id: 'demo-delivery-transfer-success',
    subscriptionId: 'demo-webhook-vaulten-transfer',
    eventId: 'demo-event-transfer-1042',
    webhookUrl: 'https://api.orbitpay.example/hooks/neo',
    statusCode: 202,
    succeeded: true,
    deliveredAt: '2026-09-09T15:42:00Z',
    trigger: 'automatic'
  },
  {
    id: 'demo-delivery-mainnet-failed',
    subscriptionId: 'demo-webhook-vaulten-mainnet',
    eventId: 'demo-event-mainnet-883',
    webhookUrl: 'https://events.example.com/vaulten',
    statusCode: 503,
    succeeded: false,
    error: 'Endpoint returned 503 after four attempts.',
    deliveredAt: '2026-09-09T13:18:00Z',
    trigger: 'automatic'
  },
  {
    id: 'demo-delivery-transfer-previous',
    subscriptionId: 'demo-webhook-vaulten-transfer',
    eventId: 'demo-event-transfer-1039',
    webhookUrl: 'https://api.orbitpay.example/hooks/neo',
    statusCode: 200,
    succeeded: true,
    deliveredAt: '2026-09-08T11:07:00Z',
    trigger: 'automatic'
  }
];

export const demoWebhookSubscriptions: WebhookSubscription[] = [
  {
    id: 'demo-webhook-vaulten-transfer',
    projectId: demoProject.id,
    name: 'Vault transfer events',
    contractHash: '0x4f3d8ad12f96e86c10b04f9b8bf5cba936d44b67',
    network: 'neo3:testnet',
    eventName: 'Transfer',
    webhookUrl: 'https://api.orbitpay.example/hooks/neo',
    headers: { 'X-Environment': 'investor-demo' },
    isEnabled: true,
    createdAt: '2026-09-02T08:00:00Z',
    updatedAt: '2026-09-09T15:42:00Z',
    latestDelivery: demoWebhookDeliveries[0]
  },
  {
    id: 'demo-webhook-vaulten-mainnet',
    projectId: demoProject.id,
    name: 'MainNet delivery monitor',
    contractHash: '0x759a982e3a0f46e9c6a3d4bdbf1d7990db2c2f18',
    network: 'neo3:mainnet',
    eventName: null,
    webhookUrl: 'https://events.example.com/vaulten',
    headers: {},
    isEnabled: true,
    createdAt: '2026-08-18T12:00:00Z',
    updatedAt: '2026-09-09T13:18:00Z',
    latestDelivery: demoWebhookDeliveries[1]
  }
];

export const demoRelayUsage: Record<string, RelayUsage> = {
  'neo3:testnet': {
    plan: 'free_beta',
    status: 'active',
    periodEndsAt: '2026-10-01T00:00:00Z',
    graceEndsAt: null,
    maxActiveSubscriptions: 5,
    activeSubscriptions: 1,
    maxEvents: 1000,
    eventsUsed: 184,
    eventsRemaining: 816
  },
  'neo3:mainnet': {
    plan: 'paid',
    status: 'active',
    periodEndsAt: '2026-10-08T00:00:00Z',
    graceEndsAt: '2026-10-11T00:00:00Z',
    maxActiveSubscriptions: 5,
    activeSubscriptions: 1,
    maxEvents: 10000,
    eventsUsed: 1267,
    eventsRemaining: 8733
  }
};

export const demoRelayPaymentHistory: RelayPaymentHistory = {
  payments: [
    {
      transactionId: '0x427423612453c6d562a8f90c2e44bfa0bd71d440b0a31ea00537a1d4565f14f8',
      intentId: 'demo-payment-confirmed',
      status: 'confirmed',
      entitlementEndsAt: '2026-10-08T00:00:00Z'
    }
  ],
  entitlements: [
    {
      network: 'neo3:mainnet',
      plan: 'paid',
      periodStart: '2026-09-08T00:00:00Z',
      periodEndsAt: '2026-10-08T00:00:00Z',
      graceEndsAt: '2026-10-11T00:00:00Z'
    }
  ],
  pendingIntents: []
};
