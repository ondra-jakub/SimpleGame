import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import {
  LlmModelInfo,
  PiskvorkyMoveInfo,
  PiskvorkyState,
} from '../../models/game.models';
import { ApiError, GameApiService } from '../../services/game-api.service';
import { GameSession } from '../../services/game-session';

type ModelsStatus = 'loading' | 'ready' | 'unavailable';

interface BoardCell {
  index: number;
  row: number;
  col: number;
  mark: string;
  winning: boolean;
  last: boolean;
  playable: boolean;
}

export const HUMAN_PLAYER = '__human__';

@Component({
  selector: 'app-piskvorky-game',
  imports: [FormsModule],
  templateUrl: './piskvorky-game.html',
  styleUrl: './piskvorky-game.css',
})
export class PiskvorkyGame {
  private readonly api = inject(GameApiService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly session = new GameSession<PiskvorkyState>();
  protected readonly view = this.session.view;
  protected readonly pending = this.session.pending;
  protected readonly error = this.session.error;

  protected readonly models = signal<LlmModelInfo[]>([]);
  protected readonly modelsStatus = signal<ModelsStatus>('loading');
  protected readonly modelsMessage = signal<string | null>(null);
  protected readonly modelsPending = signal(false);
  protected readonly selectedXModel = signal<string>(HUMAN_PLAYER);
  protected readonly selectedOModel = signal<string>(HUMAN_PLAYER);

  protected readonly humanValue = HUMAN_PLAYER;

  constructor() {
    this.loadModels();
    // Auto-chaining: whenever a confirmed response leaves a model on turn, send its move.
    // One request plays exactly one move; a failed request stops the chain for good.
    effect(() => {
      const state = this.view();
      if (!state || this.pending() || this.error() || state.inProgress || state.status !== 'playing') {
        return;
      }
      if (this.currentModel(state) === null) {
        return;
      }
      this.session.run(this.api.piskvorkyMove(state.id, null, state.revision));
    });
  }

  protected readonly anyModelSelected = computed(
    () => this.selectedXModel() !== HUMAN_PLAYER || this.selectedOModel() !== HUMAN_PLAYER,
  );

  protected readonly xMissing = computed(() => this.isMissing(this.selectedXModel()));
  protected readonly oMissing = computed(() => this.isMissing(this.selectedOModel()));

  protected readonly fallbackMove = computed<PiskvorkyMoveInfo | null>(() => {
    const move = this.view()?.lastMove ?? null;
    return move && move.source === 'heuristic' ? move : null;
  });

  protected readonly cells = computed<BoardCell[]>(() => {
    const state = this.view();
    if (!state) {
      return [];
    }
    const winning = new Set(state.winningLine ?? []);
    const modelTurn = this.currentModel(state) !== null;
    const interactive =
      state.status === 'playing' && !state.inProgress && !this.pending() && !this.error();
    const cells: BoardCell[] = [];
    for (let index = 0; index < state.board.length; index++) {
      const mark = state.board[index];
      cells.push({
        index,
        row: Math.floor(index / state.size),
        col: index % state.size,
        mark,
        winning: winning.has(index),
        last: state.lastMove?.position === index,
        playable: interactive && mark === '.' && !modelTurn,
      });
    }
    return cells;
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
          this.modelsStatus.set(response.available ? 'ready' : 'unavailable');
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

  protected startGame(): void {
    if (this.pending() || this.view()?.inProgress) {
      return;
    }
    const xModel = this.selectedXModel() === HUMAN_PLAYER ? null : this.selectedXModel();
    const oModel = this.selectedOModel() === HUMAN_PLAYER ? null : this.selectedOModel();
    this.session.run(this.api.startPiskvorky(xModel, oModel));
  }

  protected play(index: number): void {
    const state = this.view();
    if (!state || !this.cellPlayable(state, index)) {
      return;
    }
    this.session.run(this.api.piskvorkyMove(state.id, index, state.revision));
  }

  protected refreshGame(): void {
    const state = this.view();
    if (!state || this.pending()) {
      return;
    }
    this.session.run(this.api.getPiskvorky(state.id));
  }

  protected modeLabel(state: PiskvorkyState, player: 'X' | 'O'): string {
    return (player === 'X' ? state.xModel : state.oModel) ?? 'člověk';
  }

  protected isModelTurn(state: PiskvorkyState): boolean {
    return this.currentModel(state) !== null;
  }

  protected resultTitle(state: PiskvorkyState): string | null {
    switch (state.status) {
      case 'x_won':
        return 'Vyhrál hráč X!';
      case 'o_won':
        return 'Vyhrál hráč O!';
      case 'draw':
        return 'Remíza – deska je plná!';
      default:
        return null;
    }
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

  private isMissing(selection: string): boolean {
    return selection !== HUMAN_PLAYER && !this.models().some((model) => model.name === selection);
  }

  private currentModel(state: PiskvorkyState): string | null {
    return state.current === 'X' ? state.xModel : state.oModel;
  }

  private cellPlayable(state: PiskvorkyState, index: number): boolean {
    return (
      state.status === 'playing' &&
      !state.inProgress &&
      !this.pending() &&
      !this.error() &&
      state.board[index] === '.' &&
      this.currentModel(state) === null
    );
  }
}