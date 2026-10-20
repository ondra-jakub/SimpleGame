import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { WordGame } from './word-game';
import { WordGameState } from '../../models/game.models';

function wordState(overrides: Partial<WordGameState> = {}): WordGameState {
  return {
    id: 'w-1',
    length: 5,
    maxAttempts: 6,
    status: 'playing',
    guesses: [],
    secret: null,
    ...overrides,
  };
}

describe('WordGame', () => {
  let fixture: ComponentFixture<WordGame>;
  let http: HttpTestingController;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WordGame],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(WordGame);
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
  });

  it('normalizes the typed guess and renders a stable grid with accessible feedback', () => {
    el.querySelector<HTMLButtonElement>('.btn-start')?.click();
    http.expectOne({ method: 'POST', url: '/api/games/word/start' }).flush(wordState());
    fixture.detectChanges();

    const input = el.querySelector<HTMLInputElement>('#word-input');
    if (!input) {
      throw new Error('Word input not found');
    }
    input.value = 'kočka+';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(input.value).toBe('KOCKA');

    el.querySelector<HTMLButtonElement>('.btn-submit')?.click();
    const request = http.expectOne({ method: 'POST', url: '/api/games/word/w-1/guess' });
    expect(request.request.body).toEqual({ word: 'KOCKA' });
    request.flush(
      wordState({
        guesses: [
          {
            word: 'KOCKA',
            feedback: ['correct', 'absent', 'absent', 'absent', 'present'],
          },
        ],
      }),
    );
    fixture.detectChanges();

    const rows = el.querySelectorAll('.grid-row');
    expect(rows.length).toBe(6);
    expect(rows[0].querySelectorAll('.cell').length).toBe(5);
    expect(rows[0].querySelectorAll('.cell-correct').length).toBe(1);
    expect(rows[0].querySelectorAll('.cell-present').length).toBe(1);
    expect(rows[0].querySelectorAll('.cell-absent').length).toBe(3);
    expect(rows[5].querySelectorAll('.cell').length).toBe(5);

    const correctCell = rows[0].querySelector<HTMLElement>('.cell-correct');
    expect(correctCell?.getAttribute('aria-label')).toContain('na správném místě');
    const presentCell = rows[0].querySelector<HTMLElement>('.cell-present');
    expect(presentCell?.getAttribute('aria-label')).toContain('obsaženo jinde');

    const summary = el.querySelector('.sr-only[aria-live="polite"]')?.textContent ?? '';
    expect(summary).toContain('Tip KOCKA');
  });
});
