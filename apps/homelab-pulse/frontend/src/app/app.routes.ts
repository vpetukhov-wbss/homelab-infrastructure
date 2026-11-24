import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    title: 'Overview · HomeLab Pulse',
    loadComponent: () => import('./overview/overview').then((m) => m.OverviewPage),
  },
  {
    path: 'machines',
    title: 'Machines · HomeLab Pulse',
    loadComponent: () => import('./dashboard/machines').then((m) => m.MachinesPage),
  },
  {
    path: 'machines/:alias',
    title: 'Machine · HomeLab Pulse',
    loadComponent: () => import('./dashboard/machine-detail').then((m) => m.MachineDetailPage),
  },
  {
    path: 'services',
    title: 'Services · HomeLab Pulse',
    loadComponent: () => import('./dashboard/services').then((m) => m.ServicesPage),
  },
  {
    path: 'engineering',
    title: 'Engineering · HomeLab Pulse',
    loadComponent: () => import('./dashboard/engineering').then((m) => m.EngineeringPage),
  },
  { path: '**', redirectTo: '' },
];
