import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class NeoVmResultFormatterService {
  format(value: unknown): string {
    return JSON.stringify(this.withDecodedStackValues(value), null, 2);
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
      const binary = atob(value);
      const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
      const decoded = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
      const normalized = decoded.trim();

      return normalized && this.isReadableText(normalized) ? normalized : null;
    } catch {
      return null;
    }
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
