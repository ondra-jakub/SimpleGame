import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { App } from './app';

describe('App', () => {
  let fixture: ComponentFixture<App>;
  let http: HttpTestingController;
  let el: HTMLElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(App);
    http = TestBed.inject(HttpTestingController);
    el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
  });

  // The RPG and piškvorky panels mount eagerly and each asks for the model list.
  function flushModelCatalogs(): void {
    const requests = http.match('/api/llm/models');
    expect(requests.length).toBe(2);
    for (const request of requests) {
      request.flush({ available: true, models: [] });
    }
    fixture.detectChanges();
  }

  it('creates the shell', () => {
    flushModelCatalogs();
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders all six game tabs and switches panels', () => {
    flushModelCatalogs();

    const tabs = Array.from(el.querySelectorAll<HTMLButtonElement>('.tab'));
    expect(tabs.map((tab) => tab.textContent?.trim())).toEqual([
      'Číslo',
      'Šibenice',
      'Slovo',
      '20 otázek',
      'Souboj',
      'Piškvorky',
    ]);

    const numberPanel = el.querySelector<HTMLElement>('#panel-number');
    const rpgPanel = el.querySelector<HTMLElement>('#panel-rpg');
    const piskvorkyPanel = el.querySelector<HTMLElement>('#panel-piskvorky');
    expect(numberPanel?.hasAttribute('hidden')).toBe(false);
    expect(rpgPanel?.hasAttribute('hidden')).toBe(true);
    expect(piskvorkyPanel?.hasAttribute('hidden')).toBe(true);

    tabs.find((tab) => tab.textContent?.trim() === 'Piškvorky')?.click();
    fixture.detectChanges();
    expect(piskvorkyPanel?.hasAttribute('hidden')).toBe(false);
    expect(numberPanel?.hasAttribute('hidden')).toBe(true);
    expect(el.querySelector<HTMLButtonElement>('#tab-piskvorky')?.getAttribute('aria-selected')).toBe(
      'true',
    );
    expect(el.querySelector<HTMLButtonElement>('#tab-number')?.getAttribute('aria-selected')).toBe(
      'false',
    );
  });
});
