import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PiskvorkyGame } from './piskvorky-game';
import { PiskvorkyState } from '../../models/game.models';

function piskvorkyState(overrides: Partial<PiskvorkyState> = {}): PiskvorkyState {
  return {
    id: 'piskvorky-1',
    xMode: 'human',
    xModel: null,
    oMode: 'human',
    oModel: null,
    size: 15,
    board: '.'.repeat(225),
    current: 'X',
    status: 'playing',
    winningLine: null,
    lastMove: null,
    revision: 0,
    inProgress: false,
    moveNumber: 0,
    ...overrides,
  };
}

function boardWith(...marks: [index: number, mark: string][]): string {
  const cells = '.'.repeat(225).split('');
  for (const [index, mark] of marks) {
    cells[index] = mark;
  }
  return cells.join('');
}

describe('PiskvorkyGame', () => {
  let fixture: ComponentFixture<PiskvorkyGame>;
  let component: PiskvorkyGame;
  let http: HttpTestingController;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PiskvorkyGame],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(PiskvorkyGame);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
  });

  const moveUrl = '/api/games/piskvorky/piskvorky-1/move';

  function flushModels(
    models: { name: string; sizeBytes: number }[] = [{ name: 'llama3.2:3b', sizeBytes: 2_051_000_000 }],
  ): void {
    http.expectOne('/api/llm/models').flush({ available: true, models });
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

  function selectValue(selector: string, value: string): void {
    const select = el.querySelector<HTMLSelectElement>(selector);
    if (!select) {
      throw new Error(`Select not found: ${selector}`);
    }
    select.value = value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function startGame(response: PiskvorkyState = piskvorkyState()): void {
    clickButton('.btn-restart');
    http.expectOne({ method: 'POST', url: '/api/games/piskvorky/start' }).flush(response);
    fixture.detectChanges();
  }

  function cellButton(row: number, col: number): HTMLButtonElement | null {
    return el.querySelector<HTMLButtonElement>(
      `button[aria-label="Prázdné pole, řádek ${row}, sloupec ${col}"]`,
    );
  }

  function cellAt(index: number): HTMLElement | null {
    return el.querySelectorAll<HTMLElement>('.cell')[index] ?? null;
  }

  it('sends both player selections in the start payload', () => {
    flushModels([
      { name: 'llama3.2:3b', sizeBytes: 2_051_000_000 },
      { name: 'qwen3:0.6b', sizeBytes: 525_000_000 },
    ]);
    selectValue('#x-model-select', 'llama3.2:3b');
    selectValue('#o-model-select', 'qwen3:0.6b');

    clickButton('.btn-restart');
    const request = http.expectOne({ method: 'POST', url: '/api/games/piskvorky/start' });
    expect(request.request.body).toEqual({ xModel: 'llama3.2:3b', oModel: 'qwen3:0.6b' });
    request.flush(piskvorkyState());
    fixture.detectChanges();
    // The server opened a human-versus-human game; nothing is sent automatically.
    http.expectNone({ method: 'POST', url: moveUrl });
  });

  it('defaults both players to humans in the start payload', () => {
    flushModels();
    clickButton('.btn-restart');
    const request = http.expectOne({ method: 'POST', url: '/api/games/piskvorky/start' });
    expect(request.request.body).toEqual({ xModel: null, oModel: null });
    request.flush(piskvorkyState());
    fixture.detectChanges();
    expect(el.querySelectorAll('.cell').length).toBe(225);
  });

  it('renders the board from the state string with marks and coordinates', () => {
    flushModels();
    startGame(
      piskvorkyState({
        board: boardWith([0, 'X'], [113, 'O']),
        current: 'O',
        revision: 1,
        moveNumber: 1,
        lastMove: { player: 'O', position: 113, source: 'human', model: null, fallbackReason: null },
      }),
    );

    expect(el.querySelectorAll('.cell').length).toBe(225);
    const marks = el.querySelectorAll('.cell-mark');
    expect(marks.length).toBe(2);
    expect(marks[0]?.textContent?.trim()).toBe('X');
    expect(marks[1]?.textContent?.trim()).toBe('O');
    expect(marks[1]?.classList.contains('cell-last')).toBe(true);
    expect(el.querySelectorAll('button.cell-free').length).toBe(223);
    expect(cellButton(0, 0)).toBeNull();
    expect(cellButton(0, 1)?.getAttribute('title')).toBe('řádek 0, sloupec 1');
  });

  it('sends position and expected revision on a cell click', () => {
    flushModels();
    startGame();

    cellButton(7, 7)?.click();
    fixture.detectChanges();
    const request = http.expectOne({ method: 'POST', url: moveUrl });
    expect(request.request.body).toEqual({ position: 112, expectedRevision: 0 });
    request.flush(piskvorkyState({ board: boardWith([112, 'X']), current: 'O', revision: 1 }));
    fixture.detectChanges();
    expect(cellAt(112)?.textContent?.trim()).toBe('X');
  });

  it('auto-sends the model opening move with position null', () => {
    flushModels();
    clickButton('.btn-restart');
    http
      .expectOne({ method: 'POST', url: '/api/games/piskvorky/start' })
      .flush(piskvorkyState({ xMode: 'model', xModel: 'llama3.2:3b' }));
    fixture.detectChanges();

    expect(el.textContent).toContain('Model přemýšlí');
    expect(cellButton(0, 0)?.disabled).toBe(true);

    const move = http.expectOne({ method: 'POST', url: moveUrl });
    expect(move.request.body).toEqual({ position: null, expectedRevision: 0 });
    move.flush(
      piskvorkyState({
        board: boardWith([112, 'X']),
        current: 'O',
        revision: 1,
        moveNumber: 1,
        lastMove: { player: 'X', position: 112, source: 'llm', model: 'llama3.2:3b', fallbackReason: null },
      }),
    );
    fixture.detectChanges();
    // O is a human now; no further move is sent automatically.
    http.expectNone({ method: 'POST', url: moveUrl });
    expect(cellButton(0, 0)?.disabled).toBe(false);
  });

  it('chains model-versus-model moves until the game ends', () => {
    flushModels();
    clickButton('.btn-restart');
    http
      .expectOne({ method: 'POST', url: '/api/games/piskvorky/start' })
      .flush(
        piskvorkyState({ xMode: 'model', xModel: 'llama3.2:3b', oMode: 'model', oModel: 'llama3.2:3b' }),
      );
    fixture.detectChanges();

    const first = http.expectOne({ method: 'POST', url: moveUrl });
    expect(first.request.body).toEqual({ position: null, expectedRevision: 0 });
    first.flush(
      piskvorkyState({
        xMode: 'model',
        xModel: 'llama3.2:3b',
        oMode: 'model',
        oModel: 'llama3.2:3b',
        board: boardWith([112, 'X']),
        current: 'O',
        revision: 1,
        moveNumber: 1,
        lastMove: { player: 'X', position: 112, source: 'llm', model: 'llama3.2:3b', fallbackReason: null },
      }),
    );
    fixture.detectChanges();

    const second = http.expectOne({ method: 'POST', url: moveUrl });
    expect(second.request.body).toEqual({ position: null, expectedRevision: 1 });
    second.flush(
      piskvorkyState({
        xMode: 'model',
        xModel: 'llama3.2:3b',
        oMode: 'model',
        oModel: 'llama3.2:3b',
        board: boardWith([112, 'X'], [113, 'O']),
        current: 'X',
        revision: 2,
        moveNumber: 2,
        status: 'x_won',
        winningLine: [112, 113, 114, 115, 116],
      }),
    );
    fixture.detectChanges();

    http.expectNone({ method: 'POST', url: moveUrl });
    expect(el.textContent).toContain('Vyhrál hráč X!');
  });

  it('stops chaining when a model move fails and shows a recovery banner', () => {
    flushModels();
    clickButton('.btn-restart');
    http
      .expectOne({ method: 'POST', url: '/api/games/piskvorky/start' })
      .flush(piskvorkyState({ xMode: 'model', xModel: 'llama3.2:3b' }));
    fixture.detectChanges();

    const move = http.expectOne({ method: 'POST', url: moveUrl });
    move.flush(
      { code: 'NETWORK', message: 'Nepodařilo se spojit se serverem.' },
      { status: 0, statusText: 'Connection refused' },
    );
    fixture.detectChanges();

    expect(el.textContent).toContain('Nepodařilo se spojit se serverem.');
    http.expectNone({ method: 'POST', url: moveUrl });
    const recover = el.querySelector<HTMLButtonElement>('.btn-recover');
    expect(recover).toBeTruthy();
    expect(recover?.disabled).toBe(false);
  });

  it('disables cells while a human move is pending', () => {
    flushModels();
    startGame();

    cellButton(0, 0)?.click();
    fixture.detectChanges();
    expect(el.querySelector<HTMLButtonElement>('button.cell-free:not(:disabled)')).toBeNull();

    http
      .expectOne({ method: 'POST', url: moveUrl })
      .flush(piskvorkyState({ board: boardWith([0, 'X']), current: 'O', revision: 1 }));
    fixture.detectChanges();
    expect(el.querySelector<HTMLButtonElement>('button.cell-free:not(:disabled)')).not.toBeNull();
  });

  it('marks a heuristic fallback visibly and not a successful model move', () => {
    flushModels();
    startGame(
      piskvorkyState({
        oMode: 'model',
        oModel: 'llama3.2:3b',
        board: boardWith([0, 'X']),
        current: 'O',
        revision: 1,
        moveNumber: 1,
        lastMove: { player: 'X', position: 0, source: 'human', model: null, fallbackReason: null },
      }),
    );

    // Model O is on turn, so the component sends its move automatically.
    const auto = http.expectOne({ method: 'POST', url: moveUrl });
    expect(auto.request.body).toEqual({ position: null, expectedRevision: 1 });
    auto.flush(
      piskvorkyState({
        oMode: 'model',
        oModel: 'llama3.2:3b',
        board: boardWith([0, 'X'], [214, 'O']),
        current: 'X',
        revision: 2,
        moveNumber: 2,
        lastMove: {
          player: 'O',
          position: 214,
          source: 'heuristic',
          model: 'llama3.2:3b',
          fallbackReason: 'Model neodpověděl včas.',
        },
      }),
    );
    fixture.detectChanges();

    expect(el.textContent).toContain('záložní heuristika');
    expect(el.textContent).toContain('Model neodpověděl včas.');

    // A human click followed by a successful LLM move must not raise the banner.
    cellButton(0, 1)?.click();
    const human = http.expectOne({ method: 'POST', url: moveUrl });
    expect(human.request.body).toEqual({ position: 1, expectedRevision: 2 });
    human.flush(
      piskvorkyState({
        oMode: 'model',
        oModel: 'llama3.2:3b',
        board: boardWith([0, 'X'], [214, 'O'], [1, 'X']),
        current: 'O',
        revision: 3,
        moveNumber: 3,
        lastMove: { player: 'X', position: 1, source: 'human', model: null, fallbackReason: null },
      }),
    );
    fixture.detectChanges();

    const llm = http.expectOne({ method: 'POST', url: moveUrl });
    expect(llm.request.body).toEqual({ position: null, expectedRevision: 3 });
    llm.flush(
      piskvorkyState({
        oMode: 'model',
        oModel: 'llama3.2:3b',
        board: boardWith([0, 'X'], [214, 'O'], [1, 'X'], [15, 'O']),
        current: 'X',
        revision: 4,
        moveNumber: 4,
        lastMove: { player: 'O', position: 15, source: 'llm', model: 'llama3.2:3b', fallbackReason: null },
      }),
    );
    fixture.detectChanges();
    expect(el.textContent).not.toContain('záložní heuristika');
  });

  it('shows the result banner and blocks cells when the game ends', () => {
    flushModels();
    startGame(
      piskvorkyState({
        board: boardWith([0, 'X'], [1, 'X'], [2, 'X'], [3, 'X'], [4, 'X']),
        status: 'x_won',
        winningLine: [0, 1, 2, 3, 4],
        revision: 9,
        moveNumber: 9,
      }),
    );

    expect(el.textContent).toContain('Vyhrál hráč X!');
    expect(el.querySelector<HTMLButtonElement>('button.cell-free:not(:disabled)')).toBeNull();
    for (const index of [0, 1, 2, 3, 4]) {
      expect(cellAt(index)?.classList.contains('cell-win')).toBe(true);
    }
    const restart = el.querySelector<HTMLButtonElement>('.btn-restart');
    expect(restart?.disabled).toBe(false);
  });

  it('recovers the confirmed state via GET after a network failure', () => {
    flushModels();
    startGame();

    cellButton(0, 0)?.click();
    const failed = http.expectOne({ method: 'POST', url: moveUrl });
    failed.flush(
      { code: 'NETWORK', message: 'Nepodařilo se spojit se serverem.' },
      { status: 0, statusText: 'Connection refused' },
    );
    fixture.detectChanges();

    clickButton('.btn-recover');
    const refresh = http.expectOne({ method: 'GET', url: '/api/games/piskvorky/piskvorky-1' });
    refresh.flush(
      piskvorkyState({ board: boardWith([0, 'X']), current: 'O', revision: 1, moveNumber: 1 }),
    );
    fixture.detectChanges();

    expect(el.querySelector<HTMLElement>('.battle-panel')?.dataset['sessionId']).toBe('piskvorky-1');
    expect(el.textContent).not.toContain('Nepodařilo se spojit se serverem.');
    expect(el.querySelector<HTMLButtonElement>('.btn-recover')).toBeFalsy();
    expect(cellAt(0)?.textContent?.trim()).toBe('X');
    expect(cellButton(0, 1)?.disabled).toBe(false);
  });

  it('keeps the model selection after an outage instead of silently starting humans', () => {
    flushModels();
    selectValue('#x-model-select', 'llama3.2:3b');
    clickButton('.btn-refresh');
    http.expectOne('/api/llm/models').flush({ available: false, models: [] });
    fixture.detectChanges();
    expect(el.querySelector<HTMLSelectElement>('#x-model-select')?.value).toBe('llama3.2:3b');

    clickButton('.btn-restart');
    const request = http.expectOne('/api/games/piskvorky/start');
    expect(request.request.body).toEqual({ xModel: 'llama3.2:3b', oModel: null });
    request.flush(
      { code: 'ollama_unavailable', message: 'Ollama není dostupná.' },
      { status: 503, statusText: 'Unavailable' },
    );
    fixture.detectChanges();
    expect(el.textContent).toContain('Ollama není dostupná.');
  });

  it('differentiates an empty model list from an unavailable Ollama', () => {
    flushModels([]);
    expect(el.textContent).toContain('nemá nainstalované žádné modely');
    const xSelect = el.querySelector<HTMLSelectElement>('#x-model-select');
    const oSelect = el.querySelector<HTMLSelectElement>('#o-model-select');
    expect(xSelect?.options.length).toBe(1);
    expect(oSelect?.options.length).toBe(1);
    expect(xSelect?.options[0]?.value).toBe('__human__');

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
});