import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Column } from './ui';
import { RxIcon } from './ui';
import { J } from '../core/api.service';

function load(key: string): J[] { try { return JSON.parse(localStorage.getItem('rx.views.' + key) ?? '[]'); } catch { return []; } }
function save(key: string, v: J[]): void { try { localStorage.setItem('rx.views.' + key, JSON.stringify(v)); } catch { /* optional convenience */ } }

/** Saved filters live in the viewer's browser (a per-viewer convenience), never on the server. */
@Component({
  selector: 'rx-saved-views', standalone: true, imports: [CommonModule, FormsModule, RxIcon],
  template: `
  <div class="row">
    <label class="field" style="flex-direction:row;align-items:center;gap:6px"><span class="small muted">Saved view</span>
      <select #sel (change)="pick(sel.value)" aria-label="Saved views">
        <option value="">Default</option>
        <ng-container *ngFor="let v of views()"> <option [value]="v.name" [selected]="v.name === active()">{{ v.name }}</option> </ng-container>
      </select></label>
    <button type="button" class="btn sm" (click)="adding.set(!adding())" [attr.aria-expanded]="adding()">Save current view</button>
    <ng-container *ngIf="active()"> <button type="button" class="btn sm ghost" (click)="remove()" [attr.aria-label]="'Delete saved view ' + active()"><rx-icon name="trash" [size]="14"/>Delete</button> </ng-container>
    <ng-container *ngIf="adding()">
      <input type="text" [(ngModel)]="name" placeholder="View name" aria-label="View name" (keydown.enter)="commit()" style="width:160px"><button type="button" class="btn sm primary" (click)="commit()" [disabled]="!name.trim()">Save</button>
    </ng-container>
  </div>`,
})
export class RxSavedViews {
  @Input({ required: true }) key = ''; @Input() state: J = {}; @Output() apply = new EventEmitter<J>();
  views = signal<J[]>([]); active = signal(''); adding = signal(false); name = '';
  ngOnInit() { this.views.set(load(this.key)); }
  pick(n: string) { this.active.set(n); const v = this.views().find(x => x.name === n); if (v) this.apply.emit(v.state); else this.apply.emit({}); }
  commit() { const n = this.name.trim(); if (!n) return; const next = [...this.views().filter(v => v.name !== n), { name: n, state: this.state }]; this.views.set(next); save(this.key, next); this.active.set(n); this.adding.set(false); this.name = ''; }
  remove() { const next = this.views().filter(v => v.name !== this.active()); this.views.set(next); save(this.key, next); this.active.set(''); }
}

@Component({
  selector: 'rx-column-chooser', standalone: true, imports: [CommonModule, RxIcon],
  template: `
  <details class="col-chooser" style="position:relative">
    <summary class="btn sm" style="list-style:none"><rx-icon name="sliders" [size]="14"/> Columns</summary>
    <div class="popover" style="top:34px;right:0;left:auto;width:230px" role="group" aria-label="Choose columns">
      <ng-container *ngFor="let c of columns"> <label class="check"><input type="checkbox" [checked]="!hidden.includes(c.key)" (change)="toggle(c.key, $any($event.target).checked)"> {{ c.label }}</label> </ng-container>
    </div>
  </details>`,
})
export class RxColumnChooser {
  @Input() columns: Column[] = []; @Input() hidden: string[] = []; @Output() hiddenChange = new EventEmitter<string[]>();
  toggle(k: string, on: boolean) { this.hiddenChange.emit(on ? this.hidden.filter(h => h !== k) : [...this.hidden, k]); }
}
