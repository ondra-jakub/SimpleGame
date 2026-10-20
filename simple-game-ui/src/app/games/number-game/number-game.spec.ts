import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { NumberGame } from './number-game';
import { NumberGameState } from '../../models/game.models';

function numberState(overrides: Partial<NumberGameState> = {}): NumberGameState {
  return {
    id: 'n-1',
    min: 1,
    max: 100,
    attempts: 0,
    status: 'playing',
    result: null,
    history: [],
    ...overrides,
  };
}

describe('NumberGame', () => {
  let fixture: ComponentFixture<NumberGame>;
  let component: NumberGame;
  let http: HttpTestingController;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [NumberGame],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(NumberGame);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
  });

  function clickButton(selector: string): void {
    const button = el.querySelector<HTMLButtonElement>(selector);
    if (!button) {
      throw new Error(`Button not found: ${selector}`);
    }
    button.click();
    fixture.detectChanges();
  }

  function setNumberInput(selector: string, value: string): void {
    const input = el.querySelector<HTMLInputElement>(selector);
    if (!input) {
      throw new Error(`Input not found: ${selector}`);
    }
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('validates the range locally and sends start payload', () => {
    setNumberInput('#num-min', '100');
    setNumberInput('#num-max', '1');
    clickButton('.btn-start');
    http.expectNone({ method: 'POST', url: '/api/games/number/start' });
    expect(el.textContent).toContain('Spodní mez musí být menší než horní.');

    setNumberInput('#num-min', '1');
    setNumberInput('#num-max', '100');
    clickButton('.btn-start');
    const request = http.expectOne({ method: 'POST', url: '/api/games/number/start' });
    expect(request.request.body).toEqual({ min: 1, max: 100 });
    request.flush(numberState());
    fixture.detectChanges();
    expect(el.textContent).toContain('Rozsah 1–100');
  });

  it('sends a guess with the session id and shows feedback', () => {
    clickButton('.btn-start');
    http.expectOne('/api/games/number/start').flush(numberState());
    fixture.detectChanges();

    setNumberInput('#num-guess', '42');
    clickButton('.btn-guess');
    const request = http.expectOne({ method: 'POST', url: '/api/games/number/n-1/guess' });
    expect(request.request.body).toEqual({ value: 42 });
    request.flush(
      numberState({ attempts: 1, result: 'higher', history: [{ value: 42, result: 'higher' }] }),
    );
    fixture.detectChanges();
    expect(el.textContent).toContain('Číslo je vyšší');
    expect(el.textContent).toContain('42');
  });

  it('ignores late responses from an older session after restart', () => {
    clickButton('.btn-start');
    http.expectOne('/api/games/number/start').flush(numberState({ id: 'n-1' }));
    fixture.detectChanges();

    setNumberInput('#num-guess', '42');
    clickButton('.btn-guess');
    const guessRequest = http.expectOne({ method: 'POST', url: '/api/games/number/n-1/guess' });

    // Restart while the guess is still pending starts a newer session.
    clickButton('.btn-restart');
    const restartRequest = http.expectOne({ method: 'POST', url: '/api/games/number/start' });
    restartRequest.flush(numberState({ id: 'n-2' }));
    fixture.detectChanges();

    // The late response of the old session must not replace the new view.
    guessRequest.flush(
      numberState({
        id: 'n-1',
        attempts: 1,
        status: 'won',
        result: 'correct',
        history: [{ value: 42, result: 'correct' }],
      }),
    );
    fixture.detectChanges();

    const playPanel = el.querySelector<HTMLElement>('.play-panel');
    expect(playPanel?.dataset['sessionId']).toBe('n-2');
    expect(el.textContent).toContain('Pokusy: 0');
    expect(el.querySelector('.history')).toBeNull();
    expect(el.textContent).not.toContain('Uhodli jste');
  });
});
