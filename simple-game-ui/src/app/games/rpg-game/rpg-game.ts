import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { LlmModelInfo, RpgAction, RpgOpponentMove, RpgState } from '../../models/game.models';
import { ApiError, GameApiService } from '../../services/game-api.service';
import { GameSession } from '../../services/game-session';

type ModelsStatus = 'loading' | 'ready' | 'unavailable';

export const CLASSIC_OPPONENT = '__classic__';

@Component({
  selector: 'app-rpg-game',
  imports: [FormsModule],
  templateUrl: './rpg-game.html',
  styleUrl: './rpg-game.css',
})
export class RpgGame {
  private readonly api = inject(GameApiService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly battle = new GameSession<RpgState>();
  protected readonly view = this.battle.view;
  protected readonly pending = this.battle.pending;
  protected readonly error = this.battle.error;

  protected readonly models = signal<LlmModelInfo[]>([]);
  protected readonly modelsStatus = signal<ModelsStatus>('loading');
  protected readonly modelsMessage = signal<string | null>(null);
  protected readonly modelsPending = signal(false);
  protected readonly selectedModel = signal<string>(CLASSIC_OPPONENT);

  constructor() {
    this.loadModels();
  }

  protected readonly modelWarning = computed(() => this.selectedModel() !== CLASSIC_OPPONENT);
  protected readonly selectedModelMissing = computed(
    () =>
      this.modelWarning() && !this.models().some((model) => model.name === this.selectedModel()),
  );

  protected readonly classicValue = CLASSIC_OPPONENT;

  protected readonly selectedLabel = computed(() =>
    this.selectedModel() === CLASSIC_OPPONENT ? 'Klasická AI' : this.selectedModel(),
  );

  protected readonly fallbackMove = computed<RpgOpponentMove | null>(() => {
    const current = this.view();
    if (!current || !current.model) {
      return null;
    }
    const move = current.lastOpponentMove;
    return move && move.source === 'heuristic' ? move : null;
  });

  protected readonly reversedLog = computed(() => {
    const current = this.view();
    return current ? [...current.log].reverse() : [];
  });

  protected loadModels(): void {
    this.modelsPending.set(true);
    this.api
      .getModels()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.models.set(response.models);
          this.modelsMessage.set(response.message ?? null);
          if (response.available) {
            this.modelsStatus.set('ready');
          } else {
            this.modelsStatus.set('unavailable');
          }
          this.modelsPending.set(false);
        },
        error: (err: unknown) => {
          this.models.set([]);
          this.modelsStatus.set('unavailable');
          this.modelsMessage.set(
            err instanceof ApiError
              ? err.message
              : 'Seznam modelů se nepodařilo načíst. Zkuste tlačítko Obnovit modely.',
          );
          this.modelsPending.set(false);
        },
      });
  }

  protected startBattle(): void {
    if (this.pending() || this.view()?.inProgress) {
      return;
    }
    const model = this.selectedModel() === CLASSIC_OPPONENT ? null : this.selectedModel();
    this.battle.run(this.api.startRpg(model));
  }

  protected act(action: RpgAction): void {
    const current = this.view();
    if (!current || !this.canAct(action)) {
      return;
    }
    this.battle.run(this.api.rpgAction(current.id, action, current.revision));
  }

  protected refreshBattle(): void {
    const current = this.view();
    if (!current || this.pending()) {
      return;
    }
    this.battle.run(this.api.getRpg(current.id));
  }

  protected canAct(action: RpgAction): boolean {
    const current = this.view();
    if (!current) {
      return false;
    }
    if (current.status !== 'playing' || current.inProgress || this.pending() || this.error()) {
      return false;
    }
    if (action === 'special' && current.playerCooldown > 0) {
      return false;
    }
    return true;
  }

  protected actionLabel(action: RpgAction): string {
    switch (action) {
      case 'attack':
        return 'Útok';
      case 'special':
        return 'Speciální útok';
      case 'heal':
        return 'Léčení';
    }
  }

  protected opponentLabel(state: RpgState): string {
    return state.model ?? 'Klasická AI';
  }

  protected formatSize(bytes: number): string {
    const units = ['B', 'kB', 'MB', 'GB'];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    const rounded = value >= 10 || unit === 0 ? Math.round(value) : Math.round(value * 10) / 10;
    return `${rounded} ${units[unit]}`;
  }
}
