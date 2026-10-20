import { Component, inject, signal } from '@angular/core';
import { NumberGameState, NumberGuessEntry } from '../../models/game.models';
import { GameApiService } from '../../services/game-api.service';
import { GameSession } from '../../services/game-session';

@Component({
  selector: 'app-number-game',
  templateUrl: './number-game.html',
  styleUrl: './number-game.css',
})
export class NumberGame {
  private readonly api = inject(GameApiService);

  protected readonly session = new GameSession<NumberGameState>();
  protected readonly view = this.session.view;
  protected readonly pending = this.session.pending;
  protected readonly error = this.session.error;

  protected readonly minInput = signal<number | null>(1);
  protected readonly maxInput = signal<number | null>(100);
  protected readonly guessInput = signal<number | null>(null);
  protected readonly formError = signal<string | null>(null);

  protected onMinInput(event: Event): void {
    this.minInput.set(parseNumberInput(event));
  }

  protected onMaxInput(event: Event): void {
    this.maxInput.set(parseNumberInput(event));
  }

  protected onGuessInput(event: Event): void {
    this.guessInput.set(parseNumberInput(event));
  }

  protected startGame(): void {
    const min = this.minInput();
    const max = this.maxInput();
    if (min === null || max === null || !Number.isSafeInteger(min) || !Number.isSafeInteger(max)) {
      this.formError.set('Zadejte celočíselné meze.');
      return;
    }
    if (min >= max) {
      this.formError.set('Spodní mez musí být menší než horní.');
      return;
    }
    if (max - min > 1_000_000) {
      this.formError.set('Rozdíl mezí může být nejvýše 1 000 000.');
      return;
    }
    this.formError.set(null);
    this.guessInput.set(null);
    this.session.run(this.api.startNumberGame(min, max));
  }

  protected restart(): void {
    const current = this.view();
    if (!current) {
      return;
    }
    this.guessInput.set(null);
    this.session.run(this.api.startNumberGame(current.min, current.max));
  }

  protected guess(): void {
    const current = this.view();
    const value = this.guessInput();
    if (!current || value === null || this.pending() || current.status !== 'playing') {
      return;
    }
    if (!Number.isSafeInteger(value) || value < current.min || value > current.max) {
      this.formError.set(`Tip musí být mezi ${current.min} a ${current.max}.`);
      return;
    }
    this.formError.set(null);
    this.session.run(this.api.guessNumber(current.id, value));
    this.guessInput.set(null);
  }

  protected reversedHistory(history: NumberGuessEntry[]): NumberGuessEntry[] {
    return [...history].reverse();
  }

  protected lastGuess(): NumberGuessEntry | null {
    const current = this.view();
    if (!current || current.history.length === 0) {
      return null;
    }
    return current.history[current.history.length - 1];
  }
}

function parseNumberInput(event: Event): number | null {
  const value = (event.target as HTMLInputElement).valueAsNumber;
  return Number.isFinite(value) ? value : null;
}
