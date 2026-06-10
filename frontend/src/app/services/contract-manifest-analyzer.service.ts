import { Injectable } from '@angular/core';

interface ManifestLike {
  abi?: {
    methods?: Array<{
      name?: string;
    }>;
  };
}

@Injectable({ providedIn: 'root' })
export class ContractManifestAnalyzerService {
  async getUpdateWarning(file: File): Promise<string> {
    try {
      const manifest = JSON.parse(await file.text()) as ManifestLike;
      const hasUpdateMethod = manifest.abi?.methods?.some((method) =>
        method.name === 'update'
      ) ?? false;

      return hasUpdateMethod
        ? ''
        : 'This contract manifest has no update method. If something is wrong after deployment, Pusharoo cannot update this contract.';
    } catch {
      return 'Pusharoo could not read this manifest yet. Make sure it is valid contract manifest JSON before uploading.';
    }
  }
}
