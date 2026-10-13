/** True for key events that come from inside the terminal: global shortcuts must leave those to the shell. */
export const fromTerminal = (e: KeyboardEvent) => e.target instanceof Element && e.target.closest(".terminal") !== null;
