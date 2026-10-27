declare module 'voidstrap' {
  export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

  export type Surface = 'desktop';

  export interface Events {
    'session.start': { input: { source: 'startup' | 'reload' }; result: unknown };
    'session.end': { input: { reason: 'exit' }; result: unknown };
    'roblox.launch': { input: { mode: 'player' | 'studio'; args: string }; result: { mode: 'player' | 'studio'; args: string; cancel?: boolean } };
    'game.join': { input: GameInfo; result: unknown };
    'game.leave': { input: GameInfo; result: unknown };
    'page.navigate': { input: { page: string }; result: unknown };
    'ui.render': { input: { component: 'Pane'; requestId: string; plugin: string; surface: Surface }; result: Node | null };
  }

  export interface GameInfo {
    placeId: number;
    universeId: number;
    jobId: string;
  }

  export type EventName = keyof Events;

  export type Next<E extends EventName> = (e?: Events[E]['input']) => Events[E]['result'];

  export type Hook<E extends EventName> = ($: Engine, e: Readonly<Events[E]['input']>, next: Next<E>) => Events[E]['result'] | Promise<Events[E]['result']> | void | Promise<void>;

  export type Matcher<E extends EventName> = Partial<Record<keyof Events[E]['input'], string | number | Array<string | number>>>;

  export interface On {
    <E extends EventName>(event: E, hook: Hook<E>): void;
    <E extends EventName>(event: E, matcher: Matcher<E>, hook: Hook<E>): void;
  }

  export type Register = (on: On, options: Record<string, Json>) => void | Promise<void>;

  export interface Node {
    type: ElementName;
    props: Record<string, unknown>;
    children: Array<Node | string | number>;
  }

  export type ElementName = 'Box' | 'Text' | 'Button' | 'Toggle' | 'TextInput' | 'Card' | 'Image' | 'Divider';

  export interface ElementProps {
    Box: { key?: string; direction?: 'row' | 'column' };
    Text: { key?: string; text?: string; bold?: boolean; size?: number; dim?: boolean };
    Button: { key?: string; label?: string; variant?: 'primary'; onPress?: () => void | Promise<void> };
    Toggle: { key?: string; label?: string; checked?: boolean; onChange?: (checked: boolean) => void | Promise<void> };
    TextInput: { key?: string; value?: string; placeholder?: string; onSubmit?: (text: string) => void | Promise<void> };
    Card: { key?: string; direction?: 'row' | 'column' };
    Image: { key?: string; src: string; height?: number };
    Divider: { key?: string };
  }

  export interface FetchRequest {
    url: string;
    method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
    headers?: Record<string, string>;
    body?: string;
    contentType?: string;
  }

  export interface FetchResult {
    status: number;
    ok: boolean;
    text: string;
  }

  export interface SidebarLayout {
    items: Array<{ key: string; name: string; section: string; canHide: boolean }>;
    order: string[];
    hidden: string[];
  }

  export interface Engine {
    log(text: unknown): void;
    app: { readonly version: string; readonly platform: 'windows' | 'linux' };
    ui: {
      resolve(e?: unknown): { [K in ElementName]: K };
      open(request: { id: string; title?: string }): { isPlaced: boolean };
      close(id: string): void;
      status(text?: string): void;
      toast(title: string, text?: string): void;
      refresh(): void;
    };
    settings: { get(name: string): Json | undefined; set(name: string, value: Json): void };
    store: { get(key: string): Json | undefined; set(key: string, value: Json): void };
    state: { get(key: string): Json | undefined; set(key: string, value: Json): void };
    clock: { now(): number; after(ms: number, fn: () => void | Promise<void>): number; every(ms: number, fn: () => void | Promise<void>): number; cancel(id: number): void };
    net: { fetch(request: string | FetchRequest): Promise<FetchResult> };
    sidebar: { get(): SidebarLayout; set(layout: { order?: string[]; hidden?: string[] }): void };
  }

  export interface Atom<T> {
    readonly ref: string;
    readonly initial: T;
  }
}

declare module 'voidstrap/testing' {
  import type { EventName, Events, Node } from 'voidstrap';
  export const test: (name: string, body: () => void | Promise<void>) => void;
  export const expect: (actual: unknown) => { toBe(expected: unknown): void; toEqual(expected: unknown): void; toBeTruthy(): void; toContain(expected: unknown): void };
  export const mod: {
    emit<E extends EventName>(event: E, e: Events[E]['input']): Events[E]['result'];
    render(paneId: string): Node | null;
    press(tree: Node | null, key: string, value?: unknown): void;
  };
}

declare function h(type: import('voidstrap').ElementName, props: Record<string, unknown> | null, ...children: unknown[]): import('voidstrap').Node;
declare const Fragment: 'Box';
declare const $: import('voidstrap').Engine;
declare function atom<T>(ref: string, initial: T): import('voidstrap').Atom<T>;
declare function read<T>($: import('voidstrap').Engine, item: import('voidstrap').Atom<T>): T;
declare function update<T>($: import('voidstrap').Engine, item: import('voidstrap').Atom<T>, change: (value: T) => T): void;
declare function test(name: string, body: () => void | Promise<void>): void;
declare function expect(actual: unknown): { toBe(expected: unknown): void; toEqual(expected: unknown): void; toBeTruthy(): void; toContain(expected: unknown): void };
declare const mod: {
  emit<E extends import('voidstrap').EventName>(event: E, e: import('voidstrap').Events[E]['input']): import('voidstrap').Events[E]['result'];
  render(paneId: string): import('voidstrap').Node | null;
  press(tree: import('voidstrap').Node | null, key: string, value?: unknown): void;
};

declare namespace JSX {
  type Element = import('voidstrap').Node;
  interface IntrinsicElements {
    [name: string]: Record<string, unknown>;
  }
}
