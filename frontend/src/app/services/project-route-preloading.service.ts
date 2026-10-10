import { Injectable } from '@angular/core';
import { PreloadingStrategy, Route } from '@angular/router';
import { catchError, Observable, of } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class ProjectRoutePreloadingStrategy implements PreloadingStrategy {
  preload(route: Route, load: () => Observable<unknown>): Observable<unknown> {
    return route.data?.['preload'] ? load().pipe(catchError(() => of(null))) : of(null);
  }
}
