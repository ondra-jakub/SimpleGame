import { Component, computed, inject, signal } from '@angular/core';
import { TwentyState } from '../../models/game.models';
import { GameApiService } from '../../services/game-api.service';
import { GameSession } from '../../services/game-session';

export const TWENTY_MAX_QUESTIONS = 20;

@Component({
  selector: 'app-twenty-game',
  templateUrl: './twenty-game.html',
  styleUrl: './twenty-game.css',
})
export class TwentyGame {
  private readonly api = inject(GameApiService);

  protected readonly session = new GameSession<TwentyState>();
  protected readonly view = this.session.view;
  protected readonly pending = this.session.pending;
  protected readonly error = this.session.error;

  protected readonly realAnswer = signal('');
  protected readonly teachQuestion = signal('');
  protected readonly formError = signal<string | null>(null);

  protected readonly maxQuestions = TWENTY_MAX_QUESTIONS;

  protected readonly remainingQuestions = computed(() => {
    const current = this.view();
    if (!current) {
      return TWENTY_MAX_QUESTIONS;
    }
    return Math.max(0, TWENTY_MAX_QUESTIONS - current.questionsAsked);
  });

  protected start(): void {
    this.realAnswer.set('');
    this.teachQuestion.set('');
    this.formError.set(null);
    this.session.run(this.api.startTwenty());
  }

  protected answer(answer: 'yes' | 'no'): void {
    const current = this.view();
    if (!current || this.pending() || current.phase !== 'question') {
      return;
    }
    this.session.run(this.api.answerTwenty(current.id, answer));
  }

  protected verdict(correct: boolean): void {
    const current = this.view();
    if (!current || this.pending() || current.phase !== 'guess') {
      return;
    }
    this.session.run(this.api.verdictTwenty(current.id, correct));
  }

  protected teach(): void {
    const current = this.view();
    if (!current || this.pending() || current.phase !== 'teach') {
      return;
    }
    const realAnswer = this.realAnswer().trim();
    const question = this.teachQuestion().trim();
    if (realAnswer.length === 0 || question.length === 0) {
      this.formError.set('Vyplňte správnou odpověď i novou otázku.');
      return;
    }
    this.formError.set(null);
    this.session.run(this.api.teachTwenty(current.id, realAnswer, question));
  }

  protected onRealAnswerInput(event: Event): void {
    this.realAnswer.set((event.target as HTMLInputElement).value);
  }

  protected onTeachQuestionInput(event: Event): void {
    this.teachQuestion.set((event.target as HTMLInputElement).value);
  }

  protected answerLabel(answer: string): string {
    if (answer === 'yes') {
      return 'Ano';
    }
    if (answer === 'no') {
      return 'Ne';
    }
    return answer;
  }
}
