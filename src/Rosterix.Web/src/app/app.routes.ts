import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { Session } from './core/auth.service';
import { Toasts } from './core/ui.services';

export const authGuard: CanActivateFn = async () => {
  const s = inject(Session); const r = inject(Router);
  if (!s.isSignedIn()) return r.parseUrl('/login');
  const me = await s.ensureMe();
  return me ? true : r.parseUrl('/login');
};

export const roleGuard: CanActivateFn = route => {
  const s = inject(Session); const r = inject(Router); const t = inject(Toasts);
  const need: string[] = route.data['roles'] ?? [];
  if (need.length === 0 || s.has(...need)) return true;
  t.error('Your role doesn’t have access to that page. Showing your home page instead.');
  return r.parseUrl('/home');
};

const R = (path: string, title: string, roles: string[] | null, load: () => Promise<any>): Routes[number] => ({ path, title: `${title} · Rosterix`, data: { title, roles: roles ?? [] }, canActivate: [roleGuard], loadComponent: load });

export const routes: Routes = [
  R('login', 'Sign in', null, () => import('./pages/login').then(m => m.LoginPage)),
  R('auth/callback', 'Signing in', null, () => import('./pages/login').then(m => m.CallbackPage)),
  {
    path: '', canActivate: [authGuard], loadComponent: () => import('./shell').then(m => m.Shell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'home' },
      R('home', 'Home', null, () => import('./pages/home').then(m => m.HomePage)),
      R('projects', 'Projects', ['PM', 'LM', 'HRBP', 'RH', 'HRSS'], () => import('./pages/projects').then(m => m.ProjectsPage)),
      R('projects/new', 'New project', ['PM'], () => import('./pages/projects').then(m => m.ProjectFormPage)),
      R('projects/:id/edit', 'Edit project', ['PM'], () => import('./pages/projects').then(m => m.ProjectFormPage)),
      R('projects/:id', 'Project', ['PM', 'LM', 'HRBP', 'RH', 'HRSS', 'EMP'], () => import('./pages/projects').then(m => m.ProjectDetailPage)),
      R('tagging', 'Employee tagging', ['PM', 'LM', 'HRBP', 'HRSS'], () => import('./pages/tagging').then(m => m.TaggingPage)),
      R('tagging/new', 'Tag employees', ['PM', 'LM'], () => import('./pages/tagging').then(m => m.TagFlowPage)),
      R('approvals', 'Approvals', ['PM', 'LM', 'HRBP', 'RH', 'HRSS'], () => import('./pages/approvals').then(m => m.ApprovalsPage)),
      R('approvals/:id', 'Approval request', ['PM', 'LM', 'HRBP', 'RH', 'HRSS'], () => import('./pages/approvals').then(m => m.ApprovalDetailPage)),
      R('roster', 'Roster', ['PM', 'LM', 'HRSS', 'HRBP', 'RH'], () => import('./pages/roster').then(m => m.RosterPage)),
      R('allowance', 'Allowance', ['PM', 'LM', 'HRSS', 'HRBP'], () => import('./pages/allowance').then(m => m.AllowancePage)),
      R('allowance/:id', 'Allowance run', ['PM', 'LM', 'HRSS', 'HRBP'], () => import('./pages/allowance').then(m => m.RunDetailPage)),
      R('exceptions', 'Exceptions', ['PM', 'LM', 'HRBP', 'HRSS'], () => import('./pages/exceptions').then(m => m.ExceptionsPage)),
      R('anomalies', 'Anomalies', ['PM', 'LM', 'HRBP', 'HRSS'], () => import('./pages/exceptions').then(m => m.AnomaliesPage)),
      R('reports', 'Reports', null, () => import('./pages/reports').then(m => m.ReportsPage)),
      R('payroll', 'Payroll', ['HRSS', 'HRBP', 'RH'], () => import('./pages/reports').then(m => m.PayrollPage)),
      R('movements', 'Movements', ['HRSS'], () => import('./pages/people').then(m => m.MovementsPage)),
      R('delegation', 'Delegation', ['PM', 'LM', 'HRSS'], () => import('./pages/people').then(m => m.DelegationPage)),
      R('arrears', 'Arrears', ['PM', 'LM', 'HRBP', 'RH', 'HRSS'], () => import('./pages/people').then(m => m.ArrearsPage)),
      R('settlement', 'Full & final settlement', ['HRSS'], () => import('./pages/people').then(m => m.SettlementPage)),
      R('me', 'My work', null, () => import('./pages/me').then(m => m.MePage)),
      R('foundation', 'Foundation data', ['HRSS'], () => import('./pages/foundation').then(m => m.FoundationPage)),
      R('notifications', 'Notifications', null, () => import('./pages/me').then(m => m.NotificationsPage)),
      R('audit', 'Audit trail', ['HRSS'], () => import('./pages/foundation').then(m => m.AuditPage)),
      R('**', 'Not found', null, () => import('./pages/home').then(m => m.NotFoundPage)),
    ],
  },
];
