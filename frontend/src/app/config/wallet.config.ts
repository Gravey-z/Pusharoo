export const walletConfig = {
  network: 'neo3:testnet',
  walletConnectProjectId: 'd77209728a8564a2571c5c87bf71b8f1',
  contractManagement: {
    'neo3:testnet': '0xfffdc93764dbaddd97c48f252a53ea4643faa3fd',
    'neo3:mainnet': '0xfffdc93764dbaddd97c48f252a53ea4643faa3fd',
    'neo3:private': '0xfffdc93764dbaddd97c48f252a53ea4643faa3fd'
  },
  rpc: {
    'neo3:testnet': 'https://testnet1.neo.coz.io:443',
    'neo3:mainnet': 'https://mainnet1.neo.coz.io:443',
    'neo3:private': ''
  }
} as const;
