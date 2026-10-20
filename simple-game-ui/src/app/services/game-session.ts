import { DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable } from 'rxjs';
import { ApiError } from './game-api.service';

/**
 * Signal state for a single game session with stale-response protection.
 *
 * Every request increments a generation counter; responses from an older
 * generation (started before a newer request or a component destroy) are
 * dropped so they can never overwrite a newer session view.
 */
export class GameSession<T extends { id: string }> {
  readonly view = signal<T | null>(null);
  readonly pending = signal(false);
  readonly error = signal<ApiError | null>(null);

  private generation = 0;
  private readonly destroyRef = inject(DestroyRef);

  run(request: Observable<T>): void {
    const gen = ++this.generation;
    this.pending.set(true);
    this.error.set(null);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (state) => {
        if (gen !== this.generation) {
          return;
        }
        this.view.set(state);
        this.pending.set(false);
      },
      error: (err: unknown) => {
        if (gen !== this.generation) {
          return;
        }
        this.pending.set(false);
        this.error.set(
          err instanceof ApiError ? err : new ApiError(0, 'UNKNOWN', 'Došlo k neočekávané chybě.'),
        );
      },
    });
  }
}
