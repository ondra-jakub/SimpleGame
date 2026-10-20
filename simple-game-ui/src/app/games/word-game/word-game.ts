import { Component, computed, inject, signal } from '@angular/core';
import { LetterFeedback, WordGameState } from '../../models/game.models';
import { GameApiService } from '../../services/game-api.service';
import { GameSession } from '../../services/game-session';
import { normalizeWordInput } from '../../utils/text';

interface WordCell {
  letter: string | null;
  feedback: LetterFeedback | null;
}

export function feedbackText(feedback: LetterFeedback): string {
  switch (feedback) {
    case 'correct':
      return 'na správném místě';
    case 'present':
      return 'obsaženo jinde';
    case 'absent':
      return 'není ve slově';
  }
}

@Component({
  selector: 'app-word-game',
  templateUrl: './word-game.html',
  styleUrl: './word-game.css',
})
export class WordGame {
  private readonly api = inject(GameApiService);

  protected readonly session = new GameSession<WordGameState>();
  protected readonly view = this.session.view;
  protected readonly pending = this.session.pending;
  protected readonly error = this.session.error;

  protected readonly draft = signal('');
  protected readonly formError = signal<string | null>(null);

  protected readonly rows = computed<WordCell[][]>(() => {
    const current = this.view();
    if (!current) {
      return [];
    }
    const rows: WordCell[][] = [];
    for (const guess of current.guesses) {
      rows.push(
        Array.from({ length: current.length }, (_, i) => ({
          letter: guess.word[i] ?? null,
          feedback: guess.feedback[i] ?? null,
        })),
      );
    }
    if (current.status === 'playing') {
      rows.push(
        Array.from({ length: current.length }, (_, i) => ({
          letter: this.draft()[i] ?? null,
          feedback: null,
        })),
      );
    }
    while (rows.length < current.maxAttempts) {
      rows.push(Array.from({ length: current.length }, () => ({ letter: null, feedback: null })));
    }
    return rows;
  });

  protected readonly lastFeedbackSummary = computed<string | null>(() => {
    const current = this.view();
    if (!current || current.guesses.length === 0) {
      return null;
    }
    const last = current.guesses[current.guesses.length - 1];
    const parts = Array.from(last.word).map(
      (letter, i) => `${letter} ${feedbackText(last.feedback[i] ?? 'absent')}`,
    );
    return `Tip ${last.word}: ${parts.join(', ')}.`;
  });

  protected start(): void {
    this.session.run(this.api.startWord());
  }

  protected getFeedbackText(feedback: LetterFeedback): string {
    return feedbackText(feedback);
  }

  protected onDraftInput(event: Event): void {
    const current = this.view();
    const length = current?.length ?? 5;
    const normalized = normalizeWordInput((event.target as HTMLInputElement).value, length);
    this.draft.set(normalized);
    (event.target as HTMLInputElement).value = normalized;
    this.formError.set(null);
  }

  protected submitGuess(): void {
    const current = this.view();
    if (!current || this.pending() || current.status !== 'playing') {
      return;
    }
    if (this.draft().length !== current.length) {
      this.formError.set(`Slovo musí mít přesně ${current.length} písmen.`);
      return;
    }
    this.formError.set(null);
    this.session.run(this.api.guessWord(current.id, this.draft()));
    this.draft.set('');
  }
}
