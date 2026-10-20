import { Component, signal } from '@angular/core';
import { HangmanGame } from './games/hangman-game/hangman-game';
import { NumberGame } from './games/number-game/number-game';
import { PiskvorkyGame } from './games/piskvorky-game/piskvorky-game';
import { RpgGame } from './games/rpg-game/rpg-game';
import { TwentyGame } from './games/twenty-game/twenty-game';
import { WordGame } from './games/word-game/word-game';

type GameTabId = 'number' | 'hangman' | 'word' | 'twenty' | 'rpg' | 'piskvorky';

interface GameTab {
  id: GameTabId;
  label: string;
  title: string;
}

@Component({
  imports: [NumberGame, HangmanGame, WordGame, TwentyGame, RpgGame, PiskvorkyGame],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected readonly activeTab = signal<GameTabId>('number');

  protected readonly tabs: readonly GameTab[] = [
    { id: 'number', label: 'Číslo', title: 'Hádej číslo' },
    { id: 'hangman', label: 'Šibenice', title: 'Šibenice' },
    { id: 'word', label: 'Slovo', title: 'Hádej slovo' },
    { id: 'twenty', label: '20 otázek', title: 'Dvacet otázek' },
    { id: 'rpg', label: 'Souboj', title: 'Souboj arén' },
    { id: 'piskvorky', label: 'Piškvorky', title: 'Piškvorky' },
  ];

  protected selectTab(tab: GameTabId): void {
    this.activeTab.set(tab);
  }

  protected onTablistKeydown(event: KeyboardEvent): void {
    const ids = this.tabs.map((tab) => tab.id);
    const currentIndex = ids.indexOf(this.activeTab());
    let nextIndex = currentIndex;
    switch (event.key) {
      case 'ArrowRight':
        nextIndex = (currentIndex + 1) % ids.length;
        break;
      case 'ArrowLeft':
        nextIndex = (currentIndex - 1 + ids.length) % ids.length;
        break;
      case 'Home':
        nextIndex = 0;
        break;
      case 'End':
        nextIndex = ids.length - 1;
        break;
      default:
        return;
    }
    event.preventDefault();
    const nextId = ids[nextIndex];
    this.activeTab.set(nextId);
    document.getElementById(`tab-${nextId}`)?.focus();
  }
}
