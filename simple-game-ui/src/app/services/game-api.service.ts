import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, throwError } from 'rxjs';
import {
  ApiErrorBody,
  HangmanState,
  LlmModelsResponse,
  NumberGameState,
  RpgAction,
  RpgState,
  TwentyState,
  WordGameState,
} from '../models/game.models';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

function readErrorField(body: unknown, key: keyof ApiErrorBody): string | null {
  if (typeof body !== 'object' || body === null || !(key in body)) {
    return null;
  }
  const value = (body as Record<string, unknown>)[key];
  return typeof value === 'string' && value.trim().length > 0 ? value : null;
}

function toApiError(err: unknown): ApiError {
  if (err instanceof HttpErrorResponse) {
    const code = readErrorField(err.error, 'code') ?? `HTTP_${err.status}`;
    const fallbackMessage =
      err.status === 0
        ? 'Nepodařilo se spojit se serverem. Zkontrolujte, zda běží API.'
        : `Server vrátil chybu (HTTP ${err.status}).`;
    const message = readErrorField(err.error, 'message') ?? fallbackMessage;
    return new ApiError(err.status, code, message);
  }
  return new ApiError(0, 'UNKNOWN', 'Došlo k neočekávané chybě.');
}

@Injectable({ providedIn: 'root' })
export class GameApiService {
  private readonly http = inject(HttpClient);
  private readonly gamesUrl = '/api/games';

  startNumberGame(min?: number, max?: number): Observable<NumberGameState> {
    return this.post<NumberGameState>(`${this.gamesUrl}/number/start`, { min, max });
  }

  guessNumber(id: string, value: number): Observable<NumberGameState> {
    return this.post<NumberGameState>(`${this.gamesUrl}/number/${encodeURIComponent(id)}/guess`, {
      value,
    });
  }

  startHangman(): Observable<HangmanState> {
    return this.post<HangmanState>(`${this.gamesUrl}/hangman/start`, {});
  }

  guessHangman(id: string, letter: string): Observable<HangmanState> {
    return this.post<HangmanState>(`${this.gamesUrl}/hangman/${encodeURIComponent(id)}/guess`, {
      letter,
    });
  }

  startWord(): Observable<WordGameState> {
    return this.post<WordGameState>(`${this.gamesUrl}/word/start`, {});
  }

  guessWord(id: string, word: string): Observable<WordGameState> {
    return this.post<WordGameState>(`${this.gamesUrl}/word/${encodeURIComponent(id)}/guess`, {
      word,
    });
  }

  startTwenty(): Observable<TwentyState> {
    return this.post<TwentyState>(`${this.gamesUrl}/twenty/start`, {});
  }

  answerTwenty(id: string, answer: 'yes' | 'no'): Observable<TwentyState> {
    return this.post<TwentyState>(`${this.gamesUrl}/twenty/${encodeURIComponent(id)}/answer`, {
      answer,
    });
  }

  verdictTwenty(id: string, correct: boolean): Observable<TwentyState> {
    return this.post<TwentyState>(`${this.gamesUrl}/twenty/${encodeURIComponent(id)}/verdict`, {
      correct,
    });
  }

  teachTwenty(id: string, realAnswer: string, question: string): Observable<TwentyState> {
    return this.post<TwentyState>(`${this.gamesUrl}/twenty/${encodeURIComponent(id)}/teach`, {
      realAnswer,
      question,
    });
  }

  startRpg(model: string | null): Observable<RpgState> {
    return this.post<RpgState>(`${this.gamesUrl}/rpg/start`, { model });
  }

  getRpg(id: string): Observable<RpgState> {
    return this.http
      .get<RpgState>(`${this.gamesUrl}/rpg/${encodeURIComponent(id)}`)
      .pipe(catchError((err: unknown) => throwError(() => toApiError(err))));
  }

  rpgAction(id: string, action: RpgAction, expectedRevision: number): Observable<RpgState> {
    return this.post<RpgState>(`${this.gamesUrl}/rpg/${encodeURIComponent(id)}/action`, {
      action,
      expectedRevision,
    });
  }

  getModels(): Observable<LlmModelsResponse> {
    return this.http
      .get<LlmModelsResponse>('/api/llm/models')
      .pipe(catchError((err: unknown) => throwError(() => toApiError(err))));
  }

  private post<T>(url: string, body: unknown): Observable<T> {
    return this.http
      .post<T>(url, body)
      .pipe(catchError((err: unknown) => throwError(() => toApiError(err))));
  }
}
