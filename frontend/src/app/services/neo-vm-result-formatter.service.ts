import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class NeoVmResultFormatterService {
  format(value: unknown): string {
    return JSON.stringify(this.withDecodedStackValues(value), null, 2);
  }

  readableResult(value: unknown, returnType: string): string | null {
    const stack = value && typeof value === 'object'
      ? (value as { stack?: unknown }).stack
      : null;
    const first = Array.isArray(stack) ? stack[0] : null;

    if (!first || typeof first !== 'object') {
      return null;
    }

    const item = first as Record<string, unknown>;
    const rawValue = item['value'];
    const type = typeof item['type'] === 'string' ? item['type'] : '';

    if (returnType.toLowerCase() === 'hash160'
      && (type === 'Hash160' || type === 'ByteString' || type === 'Buffer')
      && typeof rawValue === 'string') {
      const bytes = this.base64Bytes(rawValue);
      return bytes?.length === 20 ? this.neoAddressFromScriptHashBytes(bytes) : null;
    }

    if (returnType.toLowerCase() === 'string'
      && (type === 'ByteString' || type === 'Buffer')
      && typeof rawValue === 'string') {
      return this.tryDecodeBase64Text(rawValue);
    }

    if (returnType.toLowerCase() === 'integer' && typeof rawValue === 'string') {
      return rawValue;
    }

    return null;
  }

  private withDecodedStackValues(value: unknown): unknown {
    if (Array.isArray(value)) {
      return value.map((item) => this.withDecodedStackValues(item));
    }

    if (!value || typeof value !== 'object') {
      return value;
    }

    const record = value as Record<string, unknown>;
    const decodedValue = this.tryDecodeStackValue(record);
    const mapped = Object.fromEntries(
      Object.entries(record).map(([key, entryValue]) => [
        key,
        this.withDecodedStackValues(entryValue)
      ])
    );

    return decodedValue === null
      ? mapped
      : {
          ...mapped,
          decodedValue
        };
  }

  private tryDecodeStackValue(item: Record<string, unknown>): string | null {
    const type = typeof item['type'] === 'string' ? item['type'] : '';
    const value = item['value'];

    if ((type !== 'ByteString' && type !== 'Buffer') || typeof value !== 'string') {
      return null;
    }

    return this.tryDecodeBase64Text(value);
  }

  private tryDecodeBase64Text(value: string): string | null {
    try {
      const bytes = this.base64Bytes(value);
      if (!bytes) {
        return null;
      }
      const decoded = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
      const normalized = decoded.trim();

      return normalized && this.isReadableText(normalized) ? normalized : null;
    } catch {
      return null;
    }
  }

  private base64Bytes(value: string): Uint8Array | null {
    try {
      const binary = atob(value);
      return Uint8Array.from(binary, (character) => character.charCodeAt(0));
    } catch {
      return null;
    }
  }

  private neoAddressFromScriptHashBytes(scriptHashBytes: Uint8Array): string {
    const payload = new Uint8Array(21);
    payload[0] = 0x35;
    payload.set(scriptHashBytes, 1);
    const checksum = this.doubleSha256(payload).slice(0, 4);
    const addressBytes = new Uint8Array(25);
    addressBytes.set(payload);
    addressBytes.set(checksum, 21);

    return this.base58Encode(addressBytes);
  }

  private doubleSha256(value: Uint8Array): Uint8Array {
    // This synchronous compact implementation is used only for display conversion.
    // Neo addresses use Base58Check(SHA-256(SHA-256(payload))).
    const hash = (input: Uint8Array): Uint8Array => {
      const words = new Uint32Array(64);
      const constants = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
      ];
      const paddedLength = ((input.length + 9 + 63) >> 6) << 6;
      const padded = new Uint8Array(paddedLength);
      padded.set(input);
      padded[input.length] = 0x80;
      const bitLength = input.length * 8;
      new DataView(padded.buffer).setUint32(padded.length - 4, bitLength);
      let h0 = 0x6a09e667, h1 = 0xbb67ae85, h2 = 0x3c6ef372, h3 = 0xa54ff53a, h4 = 0x510e527f, h5 = 0x9b05688c, h6 = 0x1f83d9ab, h7 = 0x5be0cd19;
      const rightRotate = (word: number, count: number) => (word >>> count) | (word << (32 - count));
      for (let offset = 0; offset < padded.length; offset += 64) {
        for (let index = 0; index < 16; index += 1) words[index] = new DataView(padded.buffer, offset).getUint32(index * 4);
        for (let index = 16; index < 64; index += 1) {
          const a = words[index - 15]; const b = words[index - 2];
          words[index] = (((rightRotate(a, 7) ^ rightRotate(a, 18) ^ (a >>> 3)) + words[index - 16]) + ((rightRotate(b, 17) ^ rightRotate(b, 19) ^ (b >>> 10)) + words[index - 7])) >>> 0;
        }
        let a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;
        for (let index = 0; index < 64; index += 1) {
          const s1 = rightRotate(e, 6) ^ rightRotate(e, 11) ^ rightRotate(e, 25);
          const choice = (e & f) ^ (~e & g);
          const temp1 = (h + s1 + choice + constants[index] + words[index]) >>> 0;
          const s0 = rightRotate(a, 2) ^ rightRotate(a, 13) ^ rightRotate(a, 22);
          const majority = (a & b) ^ (a & c) ^ (b & c);
          const temp2 = (s0 + majority) >>> 0;
          h = g; g = f; f = e; e = (d + temp1) >>> 0; d = c; c = b; b = a; a = (temp1 + temp2) >>> 0;
        }
        h0 = (h0 + a) >>> 0; h1 = (h1 + b) >>> 0; h2 = (h2 + c) >>> 0; h3 = (h3 + d) >>> 0;
        h4 = (h4 + e) >>> 0; h5 = (h5 + f) >>> 0; h6 = (h6 + g) >>> 0; h7 = (h7 + h) >>> 0;
      }
      const output = new Uint8Array(32); const view = new DataView(output.buffer);
      [h0, h1, h2, h3, h4, h5, h6, h7].forEach((word, index) => view.setUint32(index * 4, word));
      return output;
    };
    return hash(hash(value));
  }

  private base58Encode(value: Uint8Array): string {
    const alphabet = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
    const digits = [0];
    for (const byte of value) {
      let carry = byte;
      for (let index = 0; index < digits.length; index += 1) {
        carry += digits[index] << 8;
        digits[index] = carry % 58;
        carry = Math.floor(carry / 58);
      }
      while (carry > 0) { digits.push(carry % 58); carry = Math.floor(carry / 58); }
    }
    let result = value.findIndex((byte) => byte !== 0) === -1 ? '' : '1'.repeat(value.findIndex((byte) => byte !== 0));
    for (let index = digits.length - 1; index >= 0; index -= 1) result += alphabet[digits[index]];
    return result;
  }

  private isReadableText(value: string): boolean {
    return [...value].every((character) => {
      const codePoint = character.codePointAt(0) ?? 0;

      return character === '\n' ||
        character === '\r' ||
        character === '\t' ||
        (codePoint >= 32 && codePoint !== 127);
    });
  }
}
