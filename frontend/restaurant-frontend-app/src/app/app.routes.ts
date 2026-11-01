import { Routes } from '@angular/router';
import { RegisterComponent } from './auth/components/register/register.component';
import { LocationOverviewComponent } from './components/location-overview/location-overview.component';
import { ProfileComponent } from './auth/components/profile/profile.component';
import { MainLayoutComponent } from './shared/layouts/main-layout/main-layout.component';
import { MainPageComponent } from './pages/main-page/main-page.component';
import { ReservationConfirmedComponent } from './components/reservations/reservation-confirmed/reservation-confirmed.component';
import { TablesComponent } from './components/tables/tables/tables.component';
import { AuthGuard } from './auth/guards/auth.guard';
import { ReservationDashboardComponent } from './pages/reservation-dashboard/reservation-dashboard.component';
import { MenuPageComponent } from './pages/menu-page/menu-page.component';
import { WaiterDashboardComponent } from './pages/waiter-dashboard/waiter-dashboard.component';
import { ReportingDashboardComponent } from './pages/reporting-dashboard/reporting-dashboard.component';
import { AdminGuard } from './auth/guards/admin.guard';

export const routes: Routes = [
    { path: 'auth/callback', loadComponent: () => import('./auth/components/cognito-callback/cognito-callback.component').then(module => module.CognitoCallbackComponent) },
    {
      path: 'restaurant/reservations/:reservationId/pre-order',
      loadComponent: () => import('./pages/restaurant-order-page/restaurant-order-page.component').then(module => module.RestaurantOrderPageComponent),
    },
    {
      path: 'join/:token',
      loadComponent: () => import('./pages/guest-order-entry-page/guest-order-entry-page.component')
        .then(module => module.GuestOrderEntryPageComponent),
    },
    {
      path: 'guest-confirmation',
      loadComponent: () => import('./pages/guest-confirmation-page/guest-confirmation-page.component')
        .then(module => module.GuestConfirmationPageComponent),
    },
  {
    canActivate: [AuthGuard],
    data: { authorized: false },
    path: 'signup',
    component: RegisterComponent,
  },

  {
    path: 'locations/:id',
    component: LocationOverviewComponent,
  },

  {
    path: 'reservation-confirmed',
    component: ReservationConfirmedComponent,
  },
  {
    path: 'reservations',
    component: ReservationDashboardComponent,
    canActivate: [AuthGuard],
    data: { authorized: true },
  },
  {
    path: 'waiter-reservations',
    component: WaiterDashboardComponent,
    canActivate: [AuthGuard],
    data: { authorized: true },
  },
  {
    path: 'reports',
    component: ReportingDashboardComponent,
    canActivate: [AdminGuard],
  },

  {
    path: '',
    component: MainLayoutComponent,
    children: [
      {
        path: '',
        redirectTo: 'main',
        pathMatch: 'full',
      },
      {
        canActivate: [AuthGuard],
        data: { authorized: true },
        path: 'profile',
        component: ProfileComponent,
      },
      {
        path: 'main',
        component: MainPageComponent,
      },
      {
        canActivate: [AuthGuard],
        data: { authorized: false },
        path: 'login',
        component: MainPageComponent,
      },
      {
        path: 'menu',
        component: MenuPageComponent,
      },
      {
        path: 'tables',
        component: TablesComponent,
      },
      {
        path: 'waiter-pre-order',
        loadComponent: () =>
          import('./pages/waiter-pre-order-page/waiter-pre-order-page.component').then(
            (m) => m.WaiterPreOrderPageComponent
          ),
      },
      {
        path: 'guest-pre-order-expired',
        loadComponent: () =>
          import('./pages/guest-pre-order-expired-page/guest-pre-order-expired-page.component').then(
            (m) => m.GuestPreOrderExpiredPageComponent
          ),
      },
      {
        path: 'reservations/:reservationId/group-order',
        canActivate: [AuthGuard],
        data: { authorized: true },
        loadComponent: () => import('./pages/group-order-page/group-order-page.component').then((module) => module.GroupOrderPageComponent),
      },
      {
        path: 'join/:token',
        loadComponent: () =>
          import('./pages/guest-pre-order-page/guest-pre-order-page.component').then(
            (m) => m.GuestPreOrderPageComponent
          ),
      },
    ],
  },
  { path: '**', redirectTo: '', pathMatch: 'full' },
];
