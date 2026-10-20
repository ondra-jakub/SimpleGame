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

  it('creates the shell', () => {
    // The RPG panel mounts eagerly and asks for the model list.
    http.expectOne('/api/llm/models').flush({ available: true, models: [] });
    fixture.detectChanges();
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders all five game tabs and switches panels', () => {
    // The RPG panel mounts eagerly and asks for the model list.
    http.expectOne('/api/llm/models').flush({ available: true, models: [] });
    fixture.detectChanges();

    const tabs = Array.from(el.querySelectorAll<HTMLButtonElement>('.tab'));
    expect(tabs.map((tab) => tab.textContent?.trim())).toEqual([
      'Číslo',
      'Šibenice',
      'Slovo',
      '20 otázek',
      'Souboj',
    ]);

    const numberPanel = el.querySelector<HTMLElement>('#panel-number');
    const rpgPanel = el.querySelector<HTMLElement>('#panel-rpg');
    expect(numberPanel?.hasAttribute('hidden')).toBe(false);
    expect(rpgPanel?.hasAttribute('hidden')).toBe(true);

    tabs.find((tab) => tab.textContent?.trim() === 'Souboj')?.click();
    fixture.detectChanges();
    expect(rpgPanel?.hasAttribute('hidden')).toBe(false);
    expect(numberPanel?.hasAttribute('hidden')).toBe(true);
    expect(el.querySelector<HTMLButtonElement>('#tab-rpg')?.getAttribute('aria-selected')).toBe(
      'true',
    );
    expect(el.querySelector<HTMLButtonElement>('#tab-number')?.getAttribute('aria-selected')).toBe(
      'false',
    );
  });
});
