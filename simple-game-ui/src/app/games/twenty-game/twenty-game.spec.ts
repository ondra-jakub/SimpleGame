import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TwentyGame } from './twenty-game';

describe('TwentyGame', () => {
  it('shows the server teaching result rather than claiming the rejected guess was correct', async () => {
    await TestBed.configureTestingModule({
      imports: [TwentyGame],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(TwentyGame);
    const http = TestBed.inject(HttpTestingController);
    const element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    element.querySelector<HTMLButtonElement>('.btn-accent')!.click();
    http.expectOne('/api/games/twenty/start').flush({
      id: 't-1',
      phase: 'teach',
      question: null,
      guess: 'PES',
      questionsAsked: 5,
      transcript: [],
      message: 'Naučte mě nový pojem.',
    });
    fixture.detectChanges();
    expect(element.querySelector('.restart-row button')).not.toBeNull();
    for (const [selector, value] of [
      ['#twenty-real', 'Kočka'],
      ['#twenty-question', 'Umí vrnět?'],
    ]) {
      const input = element.querySelector<HTMLInputElement>(selector)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    fixture.detectChanges();
    element
      .querySelector<HTMLFormElement>('.teach-form')!
      .dispatchEvent(new Event('submit', { cancelable: true }));
    const request = http.expectOne('/api/games/twenty/t-1/teach');
    expect(request.request.body).toEqual({ realAnswer: 'Kočka', question: 'Umí vrnět?' });
    request.flush({
      id: 't-1',
      phase: 'finished',
      question: null,
      guess: 'PES',
      questionsAsked: 5,
      transcript: [],
      message: 'Děkuji, naučil jsem se pojem KOCKA.',
    });
    fixture.detectChanges();
    expect(element.textContent).toContain('Děkuji, naučil jsem se pojem KOCKA.');
    expect(element.textContent).not.toContain('Uhodl jsem!');
    http.verify();
  });
});
