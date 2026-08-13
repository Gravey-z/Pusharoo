import { HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class ApiErrorFormatterService {
  format(error: unknown, fallback: string): string {
    if (error instanceof HttpErrorResponse) {
      const detail = this.findMessage(error.error);
      if (detail) {
        return detail;
      }

      if (error.status === 0) {
        return 'Pusharoo could not be reached. Check that the API is running and try again.';
      }

      if (error.status === 401 || error.status === 403) {
        return 'You do not have permission to perform this action with the connected wallet.';
      }

      if (error.status === 404) {
        return 'The requested resource was not found. It may have been removed.';
      }

      if (error.status === 429) {
        return 'Too many requests were sent. Wait a moment and try again.';
      }

      return error.status >= 500
        ? 'Pusharoo encountered a server error. Try again shortly.'
        : fallback;
    }

    return this.findMessage(error) || (error instanceof Error && error.message) || fallback;
  }

  private findMessage(value: unknown): string | null {
    if (typeof value === 'string' && value.trim()) {
      return value.trim();
    }

    if (!value || typeof value !== 'object') {
      return null;
    }

    const record = value as Record<string, unknown>;
    for (const key of ['error', 'description', 'message', 'exception', 'title']) {
      const message = this.findMessage(record[key]);
      if (message) {
        return message;
      }
    }

    return null;
  }
}
