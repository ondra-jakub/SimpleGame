import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { HangmanGame } from './hangman-game';
import { HangmanState } from '../../models/game.models';

function hangmanState(overrides: Partial<HangmanState> = {}): HangmanState {
  return {
    id: 'h-1',
    maskedWord: '_ _ _ _ _'.replace(/ /g, ''),
    category: 'Zvířata',
    guessed: [],
    wrong: 0,
    maxWrong: 8,
    status: 'playing',
    word: null,
    ...overrides,
  };
}

describe('HangmanGame', () => {
  let fixture: ComponentFixture<HangmanGame>;
  let http: HttpTestingController;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [HangmanGame],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(HangmanGame);
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
  });

  it('starts a game, sends single-letter guesses and reveals the SVG progressively', () => {
    el.querySelector<HTMLButtonElement>('.btn-start')?.click();
    http
      .expectOne({ method: 'POST', url: '/api/games/hangman/start' })
      .flush(hangmanState({ maskedWord: 'KO__A' }));
    fixture.detectChanges();

    expect(el.querySelectorAll('.slot').length).toBe(5);
    expect(el.querySelectorAll('.slot-hidden').length).toBe(2);
    expect(el.querySelectorAll('.gallows g[visibility="visible"]').length).toBe(0);

    const keyA = Array.from(el.querySelectorAll<HTMLButtonElement>('.key')).find(
      (button) => button.textContent?.trim() === 'A',
    );
    keyA?.click();
    fixture.detectChanges();

    const request = http.expectOne({ method: 'POST', url: '/api/games/hangman/h-1/guess' });
    expect(request.request.body).toEqual({ letter: 'A' });
    request.flush(
      hangmanState({
        maskedWord: 'KO__A',
        guessed: ['A'],
        wrong: 2,
      }),
    );
    fixture.detectChanges();

    const keyAfter = Array.from(el.querySelectorAll<HTMLButtonElement>('.key')).find(
      (button) => button.textContent?.trim() === 'A',
    );
    expect(keyAfter?.disabled).toBe(true);
    expect(keyAfter?.className).toContain('key-hit');
    expect(el.querySelectorAll('.gallows g[visibility="visible"]').length).toBe(2);
    expect(el.querySelector('.gallows title')?.textContent).toContain('2 z 8');
  });
});
