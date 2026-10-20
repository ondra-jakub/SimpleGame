import { Component, inject } from '@angular/core';
import { HangmanState } from '../../models/game.models';
import { GameApiService } from '../../services/game-api.service';
import { GameSession } from '../../services/game-session';
import { normalizeLetter } from '../../utils/text';

@Component({
  selector: 'app-hangman-game',
  templateUrl: './hangman-game.html',
  styleUrl: './hangman-game.css',
})
export class HangmanGame {
  private readonly api = inject(GameApiService);

  protected readonly session = new GameSession<HangmanState>();
  protected readonly view = this.session.view;
  protected readonly pending = this.session.pending;
  protected readonly error = this.session.error;

  protected readonly alphabet: readonly string[] = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');

  protected start(): void {
    this.session.run(this.api.startHangman());
  }

  protected guess(letter: string): void {
    const current = this.view();
    if (
      !current ||
      this.pending() ||
      current.status !== 'playing' ||
      current.guessed.includes(letter)
    ) {
      return;
    }
    this.session.run(this.api.guessHangman(current.id, letter));
  }

  protected slots(maskedWord: string): string[] {
    return Array.from(maskedWord);
  }

  protected isLetter(char: string): boolean {
    return /\p{L}/u.test(char);
  }

  protected slotChar(char: string): string {
    if (char === ' ') {
      return ' ';
    }
    return this.isLetter(char) ? char : '·';
  }

  protected isHit(letter: string, state: HangmanState): boolean {
    return state.guessed.includes(letter) && this.letterInWord(letter, state);
  }

  protected isMiss(letter: string, state: HangmanState): boolean {
    return state.guessed.includes(letter) && !this.letterInWord(letter, state);
  }

  protected keyLabel(letter: string, state: HangmanState): string {
    if (this.isHit(letter, state)) {
      return `Písmeno ${letter}: uhodnuto`;
    }
    if (this.isMiss(letter, state)) {
      return `Písmeno ${letter}: nebylo ve slově`;
    }
    return `Písmeno ${letter}`;
  }

  private letterInWord(letter: string, state: HangmanState): boolean {
    return Array.from(state.maskedWord).some((char) => normalizeLetter(char) === letter);
  }
}
