import { Artifact, Project, ProjectCardViewModel } from '../models/pusharoo.models';

export const demoProject: Project = {
  id: 'demo-vaulten',
  name: 'Vaulten Contract',
  description: 'Contract artifact workspace',
  createdAt: '2026-05-28T00:00:00Z'
};

export const pusharooProject: Project = {
  id: 'demo-pusharoo',
  name: 'Pusharoo',
  description: 'Deployment and artifact registry',
  createdAt: '2026-05-28T00:00:00Z'
};

export const brainTeseeProject: Project = {
  id: 'demo-braintesee',
  name: 'BrainTesee',
  description: 'Puzzle contract experiment',
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
