# Pusharoo testnet faucet contract

## Compile

From the repository root:

```powershell
dotnet tool restore
dotnet tool run nccs contracts/Pusharoo.Faucet/Pusharoo.Faucet.csproj
```

The Neo artifacts are emitted under `contracts/Pusharoo.Faucet/bin/sc/`.

## Deployment data

Pass deployment data as a two-element array of `Hash160` values, in this order:

1. Administrator script hash.
2. Relayer script hash.

The deployment transaction must be witnessed by the administrator. The accounts must be non-zero and distinct.
