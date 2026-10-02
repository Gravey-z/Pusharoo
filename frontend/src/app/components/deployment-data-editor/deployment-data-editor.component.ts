import { Component, EventEmitter, Input, Output } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DeploymentDataValue } from '../../models/pusharoo.models';
import { DeploymentDataService } from '../../services/deployment-data.service';

export interface EditableDeploymentData {
  type: string;
  value: unknown;
}

const DATA_TYPES = ['Any', 'String', 'Boolean', 'Integer', 'Hash160', 'ByteArray', 'Array'];

@Component({
  selector: 'app-deployment-data-editor',
  imports: [FormsModule, NgTemplateOutlet],
  templateUrl: './deployment-data-editor.component.html',
  styleUrl: './deployment-data-editor.component.scss'
})
export class DeploymentDataEditorComponent {
  @Input({ required: true }) value!: EditableDeploymentData;
  @Input() connectedWalletAddress = '';
  @Input() depth = 0;
  @Input() root = false;
  @Output() valueChange = new EventEmitter<EditableDeploymentData>();

  readonly dataTypes = DATA_TYPES;

  constructor(private readonly deploymentData: DeploymentDataService) {}

  arrayChildren(node: EditableDeploymentData): EditableDeploymentData[] {
    return Array.isArray(node.value) ? node.value as EditableDeploymentData[] : [];
  }

  addArrayItem(node: EditableDeploymentData): void {
    this.arrayChildren(node).push({ type: 'Any', value: null });
    this.emitChange();
  }

  removeArrayItem(node: EditableDeploymentData, index: number): void {
    this.arrayChildren(node).splice(index, 1);
    this.emitChange();
  }

  moveArrayItem(node: EditableDeploymentData, index: number, offset: number): void {
    const items = this.arrayChildren(node);
    const target = index + offset;
    if (target < 0 || target >= items.length) return;
    [items[index], items[target]] = [items[target], items[index]];
    this.emitChange();
  }

  defaultValue(type: string): unknown {
    return this.initialValue(type);
  }

  emitChange(): void {
    this.valueChange.emit(this.value);
  }

  fieldError(node: EditableDeploymentData): string {
    if (node.type === 'Array') return '';
    try {
      this.deploymentData.normalize(node as DeploymentDataValue);
      return '';
    } catch (error) {
      return error instanceof Error ? error.message : 'Enter a valid value.';
    }
  }

  resolveHash(node: EditableDeploymentData): string {
    if (node.type !== 'Hash160' || typeof node.value !== 'string' || !node.value.trim()) return '';
    try {
      return this.deploymentData.normalize(node as DeploymentDataValue).value as string;
    } catch {
      return '';
    }
  }

  private initialValue(type: string): unknown {
    switch (type) {
      case 'Any': return null;
      case 'String':
      case 'Integer':
      case 'Hash160':
      case 'ByteArray': return '';
      case 'Boolean': return false;
      case 'Array': return [];
      default: return null;
    }
  }
}
