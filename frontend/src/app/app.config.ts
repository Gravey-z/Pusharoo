import {
  ApplicationConfig,
  LOCALE_ID,
  inject,
  provideAppInitializer,
  provideZoneChangeDetection
} from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { registerLocaleData } from '@angular/common';
import localeEnGb from '@angular/common/locales/en-GB';
import { provideRouter, withPreloading } from '@angular/router';

import { routes } from './app.routes';
import { RuntimeConfigService } from './services/runtime-config.service';
import { walletAuthInterceptor } from './services/wallet-auth.interceptor';
import { ProjectRoutePreloadingStrategy } from './services/project-route-preloading.service';

registerLocaleData(localeEnGb);

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideHttpClient(withInterceptors([walletAuthInterceptor])),
    provideRouter(routes, withPreloading(ProjectRoutePreloadingStrategy)),
    { provide: LOCALE_ID, useValue: 'en-GB' },
    provideAppInitializer(() => inject(RuntimeConfigService).load())
  ]
};
