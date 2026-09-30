import { Injectable } from '@angular/core';
import { addressToScriptHash } from 'neo-n3-walletkit';
import { DeploymentDataValue } from '../models/pusharoo.models';

const MAXIMUM_DEPTH = 8;
const MAXIMUM_NODES = 128;
const MAXIMUM_SERIALIZED_BYTES = 16 * 1024;
const MAXIMUM_INTEGER = (1n << 255n) - 1n;
const MINIMUM_INTEGER = -(1n << 255n);

@Injectable({ providedIn: 'root' })
export class DeploymentDataService {
  normalize(input: unknown): DeploymentDataValue {
    const state = { nodes: 0 };
    const value = this.normalizeValue(input ?? { type: 'Any', value: null }, 0, state);
    if (new TextEncoder().encode(JSON.stringify(value)).length > MAXIMUM_SERIALIZED_BYTES) {
      throw new Error(`Deployment data must be ${MAXIMUM_SERIALIZED_BYTES} bytes or smaller after normalization.`);
    }
    return value;
  }

  private normalizeValue(
    input: unknown,
    depth: number,
    state: { nodes: number }
  ): DeploymentDataValue {
    state.nodes++;
    if (state.nodes > MAXIMUM_NODES) {
      throw new Error(`Deployment data cannot contain more than ${MAXIMUM_NODES} values.`);
    }
    if (depth > MAXIMUM_DEPTH) {
      throw new Error(`Deployment data cannot be nested more than ${MAXIMUM_DEPTH} levels.`);
    }
    if (!input || typeof input !== 'object' || Array.isArray(input)) {
      throw new Error('Deployment data must be a typed object with type and value properties.');
    }

    const entry = input as Record<string, unknown>;
    if (Object.keys(entry).length !== 2 || !Object.hasOwn(entry, 'type') || !Object.hasOwn(entry, 'value')) {
      throw new Error('Deployment data must contain only type and value properties.');
    }

    switch (entry['type']) {
      case 'Any':
        if (entry['value'] !== null) throw new Error('Any deployment data currently supports only a null value.');
        return { type: 'Any', value: null };
      case 'String':
        if (typeof entry['value'] !== 'string') throw new Error('String deployment data must have a string value.');
        if (new TextEncoder().encode(entry['value']).length > MAXIMUM_SERIALIZED_BYTES) {
          throw new Error(`Deployment data must be ${MAXIMUM_SERIALIZED_BYTES} bytes or smaller after normalization.`);
        }
        return { type: 'String', value: entry['value'] };
      case 'Boolean':
        if (typeof entry['value'] !== 'boolean') throw new Error('Boolean deployment data must be true or false.');
        return { type: 'Boolean', value: entry['value'] };
      case 'Integer':
        return { type: 'Integer', value: this.normalizeInteger(entry['value']) };
      case 'Hash160':
        return { type: 'Hash160', value: this.normalizeHash160(entry['value']) };
      case 'ByteArray':
        return { type: 'ByteArray', value: this.normalizeByteArray(entry['value']) };
      case 'Array':
        if (!Array.isArray(entry['value'])) throw new Error('Array deployment data must contain an array value.');
        return {
          type: 'Array',
          value: entry['value'].map((child: unknown) => this.normalizeValue(child, depth + 1, state))
        };
      default:
        throw new Error(`Deployment data type '${String(entry['type'])}' is not supported.`);
    }
  }

  private normalizeInteger(value: unknown): string {
    if (typeof value !== 'string' || !/^[+-]?\d+$/.test(value)) {
      throw new Error('Integer deployment data must be a decimal integer string.');
    }
    if (value.length > 78) {
      throw new Error("Integer deployment data must fit within Neo's signed 256-bit integer range.");
    }
    const integer = BigInt(value);
    if (integer < MINIMUM_INTEGER || integer > MAXIMUM_INTEGER) {
      throw new Error("Integer deployment data must fit within Neo's signed 256-bit integer range.");
    }
    return integer.toString(10);
  }

  private normalizeHash160(value: unknown): string {
    if (typeof value !== 'string') {
      throw new Error('Hash160 deployment data must be a Neo N3 address or script hash.');
    }
    const candidate = value.trim();
    if (candidate.length > 64) {
      throw new Error('Hash160 deployment data must be a valid Neo N3 wallet address or 20-byte script hash.');
    }
    const scriptHash = candidate.startsWith('0x') || candidate.startsWith('0X')
      ? candidate.slice(2)
      : candidate;
    if (/^[0-9a-fA-F]{40}$/.test(scriptHash)) {
      return `0x${scriptHash.toLowerCase()}`;
    }
    try {
      const resolved = addressToScriptHash(candidate).replace(/^0x/i, '').toLowerCase();
      if (!/^[0-9a-f]{40}$/.test(resolved)) throw new Error();
      return `0x${resolved}`;
    } catch {
      throw new Error('Hash160 deployment data must be a valid Neo N3 wallet address or 20-byte script hash.');
    }
  }

  private normalizeByteArray(value: unknown): string {
    if (typeof value !== 'string') {
      throw new Error('ByteArray deployment data must be an even-length hexadecimal string.');
    }
    const hex = value.trim().replace(/^0x/i, '');
    if (hex.length > MAXIMUM_SERIALIZED_BYTES * 2 || hex.length % 2 !== 0 || !/^[0-9a-fA-F]*$/.test(hex)) {
      throw new Error('ByteArray deployment data must be an even-length hexadecimal string.');
    }
    return hex.toLowerCase();
  }
}
