import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RpgGame } from './rpg-game';
import { RpgState } from '../../models/game.models';

function battleState(overrides: Partial<RpgState> = {}): RpgState {
  return {
    id: 'rpg-1',
    model: null,
    opponentMode: 'classic',
    playerHp: 100,
    opponentHp: 100,
    playerCooldown: 0,
    opponentCooldown: 0,
    round: 0,
    revision: 0,
    inProgress: false,
    status: 'playing',
    log: [],
    lastOpponentMove: null,
    ...overrides,
  };
}

describe('RpgGame', () => {
  let fixture: ComponentFixture<RpgGame>;
  let component: RpgGame;
  let http: HttpTestingController;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RpgGame],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(RpgGame);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
  });

  function flushModels(
    body: {
      available: boolean;
      models?: { name: string; sizeBytes: number; capabilities?: string[] }[];
      message?: string;
    } = {
      available: true,
      models: [
        { name: 'd1:latest', sizeBytes: 2_051_000_000, capabilities: ['decision', 'vision'] },
      ],
    },
  ): void {
    const request = http.expectOne('/api/llm/models');
    request.flush(body);
    fixture.detectChanges();
  }

  function clickButton(selector: string): void {
    const button = el.querySelector<HTMLButtonElement>(selector);
    if (!button) {
      throw new Error(`Button not found: ${selector}`);
    }
    button.click();
    fixture.detectChanges();
  }

  function startBattle(): void {
    clickButton('.btn-restart');
    http.expectOne({ method: 'POST', url: '/api/games/rpg/start' }).flush(battleState());
    fixture.detectChanges();
  }

  it('sends the selected model in the start payload and shows it as the real opponent', () => {
    flushModels();
    const select = el.querySelector<HTMLSelectElement>('#model-select');
    if (!select) {
      throw new Error('Model select not found');
    }
    select.value = 'd1:latest';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    clickButton('.btn-restart');
    const request = http.expectOne({ method: 'POST', url: '/api/games/rpg/start' });
    expect(request.request.body).toEqual({ model: 'd1:latest' });
    request.flush(battleState({ model: 'd1:latest', opponentMode: 'decision' }));
    fixture.detectChanges();
    expect(el.textContent).toContain('Souboj s');
    expect(el.textContent).toContain('d1:latest');
    expect(el.textContent).toContain('Decision model');
  });

  it('offers only decision capable models in the opponent dropdown', () => {
    flushModels({
      available: true,
      models: [
        { name: 'd1:latest', sizeBytes: 3_730_544_597, capabilities: ['decision', 'vision'] },
        {
          name: 'qwen3.5:0.8b',
          sizeBytes: 520_000_000,
          capabilities: ['completion', 'vision', 'tools'],
        },
        { name: 'legacy:model', sizeBytes: 1_000 },
      ],
    });

    const select = el.querySelector<HTMLSelectElement>('#model-select');
    const values = Array.from(select?.options ?? []).map((option) => option.value);
    expect(values).toEqual(['__classic__', 'd1:latest']);
  });

  it('warns when no installed model supports the decision mode', () => {
    flushModels({
      available: true,
      models: [{ name: 'qwen3.5:0.8b', sizeBytes: 520_000_000, capabilities: ['completion'] }],
    });
    expect(el.textContent).toContain('neumí decision režim');
    const select = el.querySelector<HTMLSelectElement>('#model-select');
    expect(Array.from(select?.options ?? []).map((option) => option.value)).toEqual([
      '__classic__',
    ]);
  });

  it('sends model null for the classic AI opponent', () => {
    flushModels();
    clickButton('.btn-restart');
    const request = http.expectOne({ method: 'POST', url: '/api/games/rpg/start' });
    expect(request.request.body).toEqual({ model: null });
    request.flush(battleState());
  });

  it('keeps the chosen model after an outage instead of silently starting classic AI', () => {
    flushModels();
    const select = el.querySelector<HTMLSelectElement>('#model-select')!;
    select.value = 'd1:latest';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    clickButton('.btn-refresh');
    http.expectOne('/api/llm/models').flush({ available: false, models: [] });
    fixture.detectChanges();
    expect(select.value).toBe('d1:latest');
    clickButton('.btn-restart');
    const request = http.expectOne({ method: 'POST', url: '/api/games/rpg/start' });
    expect(request.request.body).toEqual({ model: 'd1:latest' });
    request.flush(
      { code: 'ollama_unavailable', message: 'Zvolte klasickou AI.' },
      { status: 503, statusText: 'Unavailable' },
    );
  });

  it('changes only the next opponent selection without replacing the current battle', () => {
    flushModels();
    startBattle();
    const select = el.querySelector<HTMLSelectElement>('#model-select')!;
    select.value = 'd1:latest';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(el.querySelector('.battle-head')?.textContent).toContain('Klasická AI');
    http.expectNone('/api/games/rpg/start');
    expect(el.querySelector<HTMLElement>('.battle-panel')?.dataset['sessionId']).toBe('rpg-1');
  });

  it('disables actions and restart while the opponent move is pending', () => {
    flushModels();
    startBattle();

    clickButton('.btn-attack');
    const actionRequest = http.expectOne({ method: 'POST', url: '/api/games/rpg/rpg-1/action' });
    expect(actionRequest.request.body).toEqual({ action: 'attack', expectedRevision: 0 });
    fixture.detectChanges();

    expect(el.textContent).toContain('Protivník přemýšlí');
    const attack = el.querySelector<HTMLButtonElement>('.btn-attack');
    const special = el.querySelector<HTMLButtonElement>('.btn-special');
    const heal = el.querySelector<HTMLButtonElement>('.btn-heal');
    const restart = el.querySelector<HTMLButtonElement>('.btn-restart');
    expect(attack?.disabled).toBe(true);
    expect(special?.disabled).toBe(true);
    expect(heal?.disabled).toBe(true);
    expect(restart?.disabled).toBe(true);

    actionRequest.flush(battleState({ revision: 1, round: 1, playerHp: 92, opponentHp: 100 }));
    fixture.detectChanges();
    expect(el.querySelector<HTMLButtonElement>('.btn-attack')?.disabled).toBe(false);
    expect(el.querySelector<HTMLButtonElement>('.btn-restart')?.disabled).toBe(false);
  });

  it('allows idle confirmed turns but blocks actions and restart during a server-side turn', () => {
    flushModels();
    startBattle();
    expect(el.querySelector<HTMLButtonElement>('.btn-attack')?.disabled).toBe(false);
    clickButton('.btn-attack');
    http
      .expectOne({ method: 'POST', url: '/api/games/rpg/rpg-1/action' })
      .flush(battleState({ inProgress: true }));
    fixture.detectChanges();
    expect(el.querySelector<HTMLButtonElement>('.btn-attack')?.disabled).toBe(true);
    expect(el.querySelector<HTMLButtonElement>('.btn-restart')?.disabled).toBe(true);
    clickButton('.btn-recover');
    http.expectOne('/api/games/rpg/rpg-1').flush(battleState({ revision: 1 }));
    fixture.detectChanges();
    expect(el.querySelector<HTMLButtonElement>('.btn-attack')?.disabled).toBe(false);
  });

  it('does not offer actions after the battle ends', () => {
    flushModels();
    clickButton('.btn-restart');
    http
      .expectOne({ method: 'POST', url: '/api/games/rpg/start' })
      .flush(battleState({ status: 'won', opponentHp: 0 }));
    fixture.detectChanges();
    expect(el.querySelector('.btn-attack')).toBeNull();
    expect(el.textContent).toContain('Vítězství!');
  });

  it('marks a heuristic fallback visibly in a decision battle', () => {
    flushModels();
    startBattle();

    clickButton('.btn-attack');
    const request = http.expectOne({ method: 'POST', url: '/api/games/rpg/rpg-1/action' });
    request.flush(
      battleState({
        model: 'd1:latest',
        opponentMode: 'decision',
        revision: 1,
        round: 1,
        log: ['Záložní AI převzala tah protivníka.'],
        lastOpponentMove: {
          source: 'heuristic',
          model: 'd1:latest',
          action: 'attack',
          taunt: 'Tohle bolí, viď?',
          fallbackReason: 'Model neodpověděl včas',
          decision: null,
        },
      }),
    );
    fixture.detectChanges();

    expect(el.textContent).toContain('záložní klasická AI');
    expect(el.textContent).toContain('Model neodpověděl včas');
    expect(el.textContent).toContain('Záložní AI převzala tah protivníka.');
  });

  it('shows the decision detail of a decision move', () => {
    flushModels();
    startBattle();

    clickButton('.btn-attack');
    const request = http.expectOne({ method: 'POST', url: '/api/games/rpg/rpg-1/action' });
    request.flush(
      battleState({
        model: 'd1:latest',
        opponentMode: 'decision',
        revision: 1,
        round: 1,
        lastOpponentMove: {
          source: 'decision',
          model: 'd1:latest',
          action: 'special',
          taunt: 'Protivník váhá: útok 41 %, speciální 36 %.',
          fallbackReason: null,
          decision: {
            probabilities: { attack: 0.407, special: 0.358, heal: 0.235 },
            confidence: 0.11,
            danger: 0.72,
            dangerLabel: { true: 'Hráč je v ohrožení', false: 'Hráč je v bezpečí' },
          },
        },
      }),
    );
    fixture.detectChanges();

    expect(el.textContent).not.toContain('záložní klasická AI');
    expect(el.textContent).toContain('Rozhodnutí modelu');
    expect(el.textContent).toContain('Útok');
    expect(el.textContent).toContain('41 %');
    expect(el.textContent).toContain('Speciální útok');
    expect(el.textContent).toContain('36 %');
    expect(el.textContent).toContain('Léčení');
    expect(el.textContent).toContain('24 %');
    expect(el.textContent).toContain('Jistota');
    expect(el.textContent).toContain('11 %');
    expect(el.textContent).toContain('Hráč je v ohrožení');
    expect(el.textContent).toContain('Protivník váhá: útok 41 %, speciální 36 %.');
  });

  it('does not mark a successful decision move as a fallback', () => {
    flushModels();
    startBattle();

    clickButton('.btn-attack');
    const request = http.expectOne({ method: 'POST', url: '/api/games/rpg/rpg-1/action' });
    request.flush(
      battleState({
        model: 'd1:latest',
        opponentMode: 'decision',
        revision: 1,
        round: 1,
        lastOpponentMove: {
          source: 'decision',
          model: 'd1:latest',
          action: 'special',
          taunt: 'Cítíš tu energii?',
          fallbackReason: null,
          decision: null,
        },
      }),
    );
    fixture.detectChanges();

    expect(el.textContent).not.toContain('záložní klasická AI');
    expect(el.textContent).toContain('Cítíš tu energii?');
  });

  it('differentiates an empty model list from an unavailable Ollama', () => {
    flushModels({ available: true, models: [] });
    expect(el.textContent).toContain('nemá nainstalované žádné modely');
    const select = el.querySelector<HTMLSelectElement>('#model-select');
    expect(select?.options.length).toBe(1);
    expect(select?.options[0]?.value).toBe('__classic__');

    clickButton('.btn-refresh');
    http.expectOne('/api/llm/models').flush({
      available: false,
      models: [],
      message: 'Ollama neodpovídá na portu 11434',
    });
    fixture.detectChanges();

    expect(el.textContent).toContain('Ollama neodpovídá na portu 11434');
    expect(el.textContent).toContain('Ollama není dostupná');
  });

  it('offers state recovery after a failed action instead of retrying', () => {
    flushModels();
    startBattle();

    clickButton('.btn-attack');
    const actionRequest = http.expectOne({ method: 'POST', url: '/api/games/rpg/rpg-1/action' });
    actionRequest.flush(
      { code: 'NETWORK', message: 'Nepodařilo se spojit se serverem.' },
      { status: 0, statusText: 'Connection refused' },
    );
    fixture.detectChanges();

    expect(el.textContent).toContain('Nepodařilo se spojit se serverem.');
    const recover = el.querySelector<HTMLButtonElement>('.btn-recover');
    expect(recover).toBeTruthy();
    expect(recover?.disabled).toBe(false);
    expect(el.querySelector<HTMLButtonElement>('.btn-attack')?.disabled).toBe(true);

    // No automatic retry of the failed POST happened so far.
    http.expectNone({ method: 'POST', url: '/api/games/rpg/rpg-1/action' });

    recover?.click();
    fixture.detectChanges();
    const refreshRequest = http.expectOne({ method: 'GET', url: '/api/games/rpg/rpg-1' });
    refreshRequest.flush(battleState({ revision: 1, round: 1, playerHp: 88, opponentHp: 91 }));
    fixture.detectChanges();

    expect(el.querySelector<HTMLElement>('.battle-panel')?.dataset['sessionId']).toBe('rpg-1');
    expect(el.textContent).toContain('Kolo 1');
    expect(el.textContent).toContain('88');
    expect(el.textContent).not.toContain('Nepodařilo se spojit se serverem.');
    expect(el.querySelector<HTMLButtonElement>('.btn-recover')).toBeFalsy();
    expect(el.querySelector<HTMLButtonElement>('.btn-attack')?.disabled).toBe(false);
  });
});
