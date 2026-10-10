import { Pipe, PipeTransform } from '@angular/core';

const inr = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 0 });
export const money = (v: number | null | undefined): string => (v === null || v === undefined ? '—' : '₹' + inr.format(Math.round(v)));
export const dateFmt = (d: string | Date | null | undefined, withYear = true): string => {
  if (!d) return '—';
  const dt = typeof d === 'string' ? new Date(d.length === 10 ? d + 'T00:00:00' : d) : d;
  return dt.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', ...(withYear ? { year: 'numeric' } : {}) });
};
export const dateTimeFmt = (d: string | null | undefined): string => (d ? new Date(d).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) : '—');
export const monthLabel = (m: string): string => new Date(m + '-01T00:00:00').toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });
export const isoDate = (d: Date): string => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
export const addDays = (iso: string, n: number): string => { const d = new Date(iso + 'T00:00:00'); d.setDate(d.getDate() + n); return isoDate(d); };

@Pipe({ name: 'inr', standalone: true }) export class InrPipe implements PipeTransform { transform(v: number | null | undefined) { return money(v); } }
@Pipe({ name: 'rxDate', standalone: true }) export class DatePipeRx implements PipeTransform { transform(v: string | null | undefined, withYear = true) { return dateFmt(v, withYear); } }
@Pipe({ name: 'rxDateTime', standalone: true }) export class DateTimePipeRx implements PipeTransform { transform(v: string | null | undefined) { return dateTimeFmt(v); } }
@Pipe({ name: 'rxMonth', standalone: true }) export class MonthPipe implements PipeTransform { transform(v: string | null | undefined) { return v ? monthLabel(v) : '—'; } }
