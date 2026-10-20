export type GameStatus = 'playing' | 'won' | 'lost';

export interface NumberGuessEntry {
  value: number;
  result: NumberGuessResult;
}

export type NumberGuessResult = 'correct' | 'higher' | 'lower';

export interface NumberGameState {
  id: string;
  min: number;
  max: number;
  attempts: number;
  status: 'playing' | 'won';
  result: NumberGuessResult | null;
  history: NumberGuessEntry[];
}

export interface HangmanState {
  id: string;
  maskedWord: string;
  category: string;
  guessed: string[];
  wrong: number;
  maxWrong: number;
  status: GameStatus;
  word: string | null;
}

export type LetterFeedback = 'correct' | 'present' | 'absent';

export interface WordGuessEntry {
  word: string;
  feedback: LetterFeedback[];
}

export interface WordGameState {
  id: string;
  length: number;
  maxAttempts: number;
  status: GameStatus;
  guesses: WordGuessEntry[];
  secret: string | null;
}

export interface TwentyTranscriptEntry {
  question: string;
  answer: string;
}

export type TwentyPhase = 'question' | 'guess' | 'teach' | 'finished' | 'giveup';

export interface TwentyState {
  id: string;
  phase: TwentyPhase;
  question: string | null;
  guess: string | null;
  questionsAsked: number;
  transcript: TwentyTranscriptEntry[];
  message: string | null;
}

export type RpgAction = 'attack' | 'special' | 'heal';

export type RpgOpponentSource = 'classic' | 'decision' | 'heuristic';

export type RpgOpponentMode = 'classic' | 'decision';

export interface RpgDangerLabels {
  true: string | null;
  false: string | null;
}

export interface RpgDecisionDetail {
  probabilities: Record<string, number>;
  confidence: number;
  danger: number | null;
  dangerLabel: RpgDangerLabels;
}

export interface RpgOpponentMove {
  source: RpgOpponentSource;
  model: string | null;
  action: RpgAction;
  taunt: string | null;
  fallbackReason: string | null;
  decision: RpgDecisionDetail | null;
}

export interface RpgState {
  id: string;
  model: string | null;
  opponentMode: RpgOpponentMode;
  playerHp: number;
  opponentHp: number;
  playerCooldown: number;
  opponentCooldown: number;
  round: number;
  revision: number;
  inProgress: boolean;
  status: GameStatus;
  log: string[];
  lastOpponentMove: RpgOpponentMove | null;
}

export type PiskvorkyStatus = 'playing' | 'x_won' | 'o_won' | 'draw';
export type PiskvorkyPlayer = 'X' | 'O';
export type PiskvorkyPlayerMode = 'human' | 'model';
export type PiskvorkyMoveSource = 'human' | 'llm' | 'heuristic';

export interface PiskvorkyMoveInfo {
  player: PiskvorkyPlayer;
  position: number;
  source: PiskvorkyMoveSource;
  model: string | null;
  fallbackReason: string | null;
}

export interface PiskvorkyState {
  id: string;
  xMode: PiskvorkyPlayerMode;
  xModel: string | null;
  oMode: PiskvorkyPlayerMode;
  oModel: string | null;
  size: number;
  board: string;
  current: PiskvorkyPlayer;
  status: PiskvorkyStatus;
  winningLine: number[] | null;
  lastMove: PiskvorkyMoveInfo | null;
  revision: number;
  inProgress: boolean;
  moveNumber: number;
}

export interface LlmModelInfo {
  name: string;
  sizeBytes: number;
  capabilities: string[];
}

export interface LlmModelsResponse {
  available: boolean;
  models: LlmModelInfo[];
  message?: string | null;
}

export type ApiErrorBody = {
  code: string;
  message: string;
};
