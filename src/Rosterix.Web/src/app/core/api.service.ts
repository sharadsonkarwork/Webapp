import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';

// API payloads are JSON documents shaped by the server; the UI treats them as loose records.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type J = any;

export interface ApiError { status: number; title: string; extra?: J; }

export function toApiError(e: unknown): ApiError {
  if (e instanceof HttpErrorResponse) {
    const b = e.error as J;
    if (e.status === 0) return { status: 0, title: 'We can’t reach the server. Check your connection and try again.' };
    return { status: e.status, title: b?.title ?? (typeof b === 'string' && b ? b : `The request failed (${e.status}).`), extra: b?.extra };
  }
  return { status: -1, title: 'Something unexpected happened.' };
}

@Injectable({ providedIn: 'root' })
export class Api {
  private http = inject(HttpClient);
  private wrap<T>(o: Observable<T>): Observable<T> { return o.pipe(catchError(e => throwError(() => toApiError(e)))); }
  private params(p?: Record<string, J>): HttpParams {
    let hp = new HttpParams();
    for (const [k, v] of Object.entries(p ?? {})) if (v !== undefined && v !== null && v !== '') hp = hp.set(k, String(v));
    return hp;
  }
  get<T = J>(url: string, p?: Record<string, J>) { return this.wrap(this.http.get<T>(url, { params: this.params(p) })); }
  post<T = J>(url: string, body: J = {}, p?: Record<string, J>) { return this.wrap(this.http.post<T>(url, body, { params: this.params(p) })); }
  put<T = J>(url: string, body: J = {}) { return this.wrap(this.http.put<T>(url, body)); }
  blob(url: string, p?: Record<string, J>) { return this.wrap(this.http.get(url, { params: this.params(p), responseType: 'blob', observe: 'response' })); }
}

export function download(blob: Blob, name: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a'); a.href = url; a.download = name; document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}
