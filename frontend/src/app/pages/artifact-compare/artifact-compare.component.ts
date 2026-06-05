import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  Artifact,
  ArtifactComparison,
  NeoMethod,
  NeoParameter,
  ProjectOverviewViewModel
} from '../../models/pusharoo.models';
import { PusharooApiService } from '../../services/pusharoo-api.service';
import { PageShellComponent } from '../page-shell/page-shell.component';

interface ParameterEntry {
  name: string;
  type: string;
  label: string;
}

interface ParameterTypeChange {
  name: string;
  fromType: string;
  toType: string;
}

interface ParameterDiff {
  added: ParameterEntry[];
  removed: ParameterEntry[];
  changed: ParameterTypeChange[];
  unchanged: ParameterEntry[];
  hasVisibleChanges: boolean;
}

interface MethodDetails {
  name: string;
  parameters: ParameterEntry[];
  returnType: string;
  safe: boolean;
}

@Component({
  selector: 'app-artifact-compare',
  imports: [FormsModule, PageShellComponent, RouterLink],
  templateUrl: './artifact-compare.component.html',
  styleUrl: './artifact-compare.component.scss'
})
export class ArtifactCompareComponent implements OnInit {
  overview: ProjectOverviewViewModel | null = null;
  artifacts: Artifact[] = [];
  comparison: ArtifactComparison | null = null;
  fromVersion = '';
  toVersion = '';
  isLoading = false;
  errorMessage = '';
  readonly projectId: string;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: PusharooApiService
  ) {
    this.projectId = this.route.snapshot.paramMap.get('projectId') ?? '';
  }

  ngOnInit(): void {
    this.api.getProjectOverview(this.projectId).subscribe((overview) => {
      this.overview = overview;
      this.artifacts = overview?.artifacts ?? [];

      if (this.artifacts.length >= 2) {
        this.toVersion = this.artifacts[0].version;
        this.fromVersion = this.artifacts[1].version;
        this.compare();
      }
    });
  }

  compare(): void {
    this.errorMessage = '';
    this.comparison = null;

    if (!this.fromVersion || !this.toVersion) {
      this.errorMessage = 'Choose two versions to compare.';
      return;
    }

    if (this.fromVersion === this.toVersion) {
      this.errorMessage = 'Choose two different versions.';
      return;
    }

    this.isLoading = true;
    this.api.compareArtifacts(this.projectId, this.fromVersion, this.toVersion).subscribe({
      next: (comparison) => {
        this.isLoading = false;
        this.comparison = comparison;
        this.errorMessage = comparison ? '' : 'Could not compare those artifact versions.';
      },
      error: () => {
        this.isLoading = false;
        this.errorMessage = 'Could not compare those artifact versions.';
      }
    });
  }

  hasChanges(comparison: ArtifactComparison): boolean {
    return (
      comparison.addedMethods.length > 0 ||
      comparison.removedMethods.length > 0 ||
      comparison.changedMethods.length > 0 ||
      comparison.addedEvents.length > 0 ||
      comparison.permissionChanges.length > 0
    );
  }

  parameterDiff(change: string): ParameterDiff | null {
    const match = /^Parameters changed from (.*) to (.*)$/.exec(change);
    if (!match) {
      return null;
    }

    const fromParameters = this.parseParameters(match[1]);
    const toParameters = this.parseParameters(match[2]);
    const fromMap = new Map(fromParameters.map((parameter) => [parameter.name, parameter]));
    const toMap = new Map(toParameters.map((parameter) => [parameter.name, parameter]));
    const added = toParameters.filter((parameter) => !fromMap.has(parameter.name));
    const removed = fromParameters.filter((parameter) => !toMap.has(parameter.name));
    const changed = toParameters
      .filter((parameter) => {
        const fromParameter = fromMap.get(parameter.name);
        return fromParameter && fromParameter.type !== parameter.type;
      })
      .map((parameter) => ({
        name: parameter.name,
        fromType: fromMap.get(parameter.name)?.type ?? '',
        toType: parameter.type
      }));
    const unchanged = toParameters.filter((parameter) => {
      const fromParameter = fromMap.get(parameter.name);
      return fromParameter && fromParameter.type === parameter.type;
    });

    return {
      added,
      removed,
      changed,
      unchanged,
      hasVisibleChanges: added.length > 0 || removed.length > 0 || changed.length > 0
    };
  }

  methodDetails(version: string, methodName: string): MethodDetails | null {
    const method = this.artifacts
      .find((artifact) => this.isVersionMatch(artifact.version, version))
      ?.manifest.abi.methods.find((artifactMethod) => artifactMethod.name === methodName);

    if (!method) {
      return null;
    }

    return {
      name: method.name,
      parameters: this.toParameterEntries(method.parameters),
      returnType: this.methodReturnType(method),
      safe: method.safe
    };
  }

  private toParameterEntries(parameters: NeoParameter[]): ParameterEntry[] {
    return parameters.map((parameter) => ({
      name: parameter.name,
      type: parameter.type,
      label: `${parameter.name}: ${parameter.type}`
    }));
  }

  private methodReturnType(method: NeoMethod): string {
    return method.returntype ?? method.returnType ?? '-';
  }

  private isVersionMatch(left: string, right: string): boolean {
    return left.trim().replace(/^v/i, '') === right.trim().replace(/^v/i, '');
  }

  private parseParameters(value: string): ParameterEntry[] {
    const trimmedValue = value.trim();
    if (!trimmedValue || trimmedValue === '-') {
      return [];
    }

    return trimmedValue.split(',').map((parameter) => {
      const [rawName, ...rawType] = parameter.trim().split(':');
      const name = rawName.trim();
      const type = rawType.join(':').trim();

      return {
        name,
        type,
        label: type ? `${name}: ${type}` : name
      };
    });
  }
}
