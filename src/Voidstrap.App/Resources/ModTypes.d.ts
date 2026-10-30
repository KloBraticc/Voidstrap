declare module 'voidstrap' {
  export type Json = null | boolean | number | string | Json[] | { [key: string]: Json };

  export type Surface = 'desktop';

  export type UiComponentKind = 'Pane' | 'Page' | 'Banner' | 'Dialog' | 'Widget';

  export interface Anchor {
    name: string;
    type: string;
    text: string;
    parent: string;
    x: number;
    y: number;
    width: number;
    height: number;
    attachable: boolean;
  }

  export interface UiTarget {
    name?: string;
    type?: string;
    text?: string;
    textContains?: string;
    parent?: string;
    ancestor?: string;
    page?: string;
    index?: number;
    all?: boolean;
  }

  export interface UiNode {
    type: string;
    name?: string;
    text?: string;
    visible: boolean;
    panel: boolean;
    x?: number;
    y?: number;
    width?: number;
    height?: number;
    children?: UiNode[];
  }

  export interface UiProperties {
    hidden?: boolean;
    rotate?: number;
    scale?: number;
    scaleX?: number;
    scaleY?: number;
    translateX?: number;
    translateY?: number;
    [property: string]: string | number | boolean | undefined;
  }

  export interface UiChangeResult {
    ok: boolean;
    matched: number;
    errors: string[];
  }

  export interface UiRedesign {
    replace?: boolean;
    resources?: Record<string, string>;
    moves?: { id: string; target: UiTarget; into: UiTarget; index?: number }[];
    rules?: { id: string; target: UiTarget; set: UiProperties }[];
  }

  export interface RobloxAccountInfo {
    signedIn: boolean;
    userId?: number;
    username?: string;
    displayName?: string;
  }

  export interface CursorPosition {
    x: number;
    y: number;
    screenX: number;
    screenY: number;
    windowWidth: number;
    windowHeight: number;
    insideWindow: boolean;
    isDown: boolean;
    button: 'none' | 'left' | 'right' | 'middle';
  }

  export interface Events {
    'session.start': { input: { source: 'startup' | 'reload' }; result: unknown };
    'session.end': { input: { reason: 'exit' }; result: unknown };
    'roblox.launch': { input: { mode: 'player' | 'studio'; args: string }; result: { mode: 'player' | 'studio'; args: string; cancel?: boolean } };
    'game.join': { input: GameInfo; result: unknown };
    'game.leave': { input: GameInfo; result: unknown };
    'page.navigate': { input: { page: string }; result: unknown };
    'settings.changed': { input: { name: string; plugin: string }; result: unknown };
    'ui.render': { input: { component: UiComponentKind; requestId: string; plugin: string; surface: Surface }; result: Node | null };
    'cursor.move': { input: CursorPosition; result: unknown };
    'cursor.down': { input: CursorPosition; result: unknown };
    'cursor.up': { input: CursorPosition; result: unknown };
    'cursor.enter': { input: CursorPosition; result: unknown };
    'cursor.leave': { input: { x: number; y: number; insideWindow: false; isDown: boolean }; result: unknown };
    'cursor.scroll': { input: CursorPosition & { delta: number }; result: unknown };
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

  export type ElementName =
    | 'Box'
    | 'Stack'
    | 'Wrap'
    | 'Grid'
    | 'Scroll'
    | 'Canvas'
    | 'Text'
    | 'Badge'
    | 'Icon'
    | 'ColorSwatch'
    | 'Button'
    | 'Toggle'
    | 'CheckBox'
    | 'TextInput'
    | 'NumberInput'
    | 'Slider'
    | 'Select'
    | 'ProgressBar'
    | 'InfoBar'
    | 'Divider'
    | 'Spacer'
    | 'Image'
    | 'Sprite'
    | 'Card'
    | 'Expander'
    | 'Tabs'
    | 'Table';

  export interface CommonStyleProps {
    key?: string;
    visible?: boolean;
    hitTestVisible?: boolean;
    tooltip?: string;
    padding?: number | string;
    margin?: number | string;
    width?: number;
    height?: number;
    minWidth?: number;
    maxWidth?: number;
    minHeight?: number;
    maxHeight?: number;
    opacity?: number;
    background?: string;
    foreground?: string;
    borderBrush?: string;
    borderThickness?: number | string;
    cornerRadius?: number;
    align?: 'left' | 'center' | 'right' | 'stretch';
    horizontalAlignment?: 'left' | 'center' | 'right' | 'stretch';
    verticalAlignment?: 'top' | 'center' | 'bottom' | 'stretch';
    row?: number;
    column?: number;
    rowSpan?: number;
    columnSpan?: number;
    left?: number;
    top?: number;
    right?: number;
    bottom?: number;
  }

  export interface ElementProps {
    Box: CommonStyleProps & { direction?: 'row' | 'column'; wrap?: boolean; gap?: number; title?: string };
    Stack: CommonStyleProps & { direction?: 'row' | 'column'; wrap?: boolean; gap?: number };
    Wrap: CommonStyleProps & { direction?: 'row' | 'column'; itemWidth?: number; itemHeight?: number };
    Grid: CommonStyleProps & { columns?: number | Array<number | string>; rows?: number | Array<number | string>; gap?: number };
    Scroll: CommonStyleProps & { maxHeight?: number; horizontal?: boolean };
    Canvas: CommonStyleProps;
    Text: CommonStyleProps & { text?: string; bold?: boolean; italic?: boolean; mono?: boolean; size?: number; dim?: boolean; color?: string; wrap?: boolean };
    Badge: CommonStyleProps & { text?: string; label?: string; variant?: 'primary' | 'success' | 'warning' | 'danger' | 'neutral' };
    Icon: CommonStyleProps & { symbol?: string; name?: string; size?: number; color?: string };
    ColorSwatch: CommonStyleProps & { color?: string; size?: number; label?: string; onPress?: () => void | Promise<void> };
    Button: CommonStyleProps & { label?: string; icon?: string; variant?: 'primary' | 'secondary' | 'danger' | 'success' | 'caution' | 'transparent'; disabled?: boolean; onPress?: () => void | Promise<void> };
    Toggle: CommonStyleProps & { label?: string; checked?: boolean; disabled?: boolean; onChange?: (checked: boolean) => void | Promise<void> };
    CheckBox: CommonStyleProps & { label?: string; checked?: boolean; disabled?: boolean; onChange?: (checked: boolean) => void | Promise<void> };
    TextInput: CommonStyleProps & { value?: string; placeholder?: string; multiline?: boolean; readOnly?: boolean; disabled?: boolean; onChange?: (text: string) => void | Promise<void>; onSubmit?: (text: string) => void | Promise<void> };
    NumberInput: CommonStyleProps & { label?: string; value?: number; min?: number; max?: number; step?: number; onChange?: (value: number) => void | Promise<void> };
    Slider: CommonStyleProps & { label?: string; value?: number; min?: number; max?: number; step?: number; onChange?: (value: number) => void | Promise<void> };
    Select: CommonStyleProps & { options?: Array<string | { value: string; label?: string }>; selected?: string; value?: string; onChange?: (value: string) => void | Promise<void> };
    ProgressBar: CommonStyleProps & { value?: number; max?: number; indeterminate?: boolean };
    InfoBar: CommonStyleProps & { title?: string; message?: string; severity?: 'info' | 'success' | 'warning' | 'error'; open?: boolean };
    Divider: CommonStyleProps;
    Spacer: CommonStyleProps;
    Image: CommonStyleProps & { src: string; height?: number; width?: number; animate?: boolean; frameWidth?: number; frameHeight?: number; frameIndex?: number; fps?: number };
    Sprite: ElementProps['Image'];
    Card: CommonStyleProps & { title?: string; subtitle?: string; direction?: 'row' | 'column'; gap?: number };
    Expander: CommonStyleProps & { title?: string; header?: string; expanded?: boolean; direction?: 'row' | 'column'; gap?: number };
    Tabs: CommonStyleProps & { selectedIndex?: number };
    Table: CommonStyleProps & { columns?: string[]; rows?: string[][] };
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

  export interface SidebarPageSpec {
    id: string;
    title?: string;
    section?: 'Core' | 'Configuration' | 'More' | 'Footer';
    icon?: string;
  }

  export interface ModPageSpec {
    id: string;
    title?: string;
    icon?: string;
  }

  export interface ModPage {
    key: string;
    id: string;
    plugin?: string;
    title: string;
    icon: string;
  }

  export interface SidebarLayout {
    items: Array<{ key: string; name: string; defaultName?: string; section: string; icon: string; canHide: boolean; isCustom?: boolean; id?: string; plugin?: string }>;
    pages: Array<{ key: string; id: string; plugin: string; name: string; section: string; icon: string }>;
    order: string[];
    hidden: string[];
    names: Record<string, string>;
    icons: Record<string, string>;
  }

  export interface ThemeState {
    theme: string;
    backdrop: string;
    uiZoomPercent: number;
    backgroundFilePath: string;
    gradientOpacity: number;
    blackOverlayOpacity: number;
    displayEverywhere: boolean;
    customThemeColor: string;
    brushes: Record<string, string>;
  }

  export interface ThemeUpdate {
    theme?: 'Default' | 'Dark' | 'Light' | string;
    backdrop?: 'Mica' | 'Acrylic' | 'Aero' | 'None' | string;
    uiZoomPercent?: number;
    backgroundFilePath?: string;
    gradientOpacity?: number;
    blackOverlayOpacity?: number;
    displayEverywhere?: boolean;
    customThemeColor?: string;
    accentColor?: string;
  }

  export interface WindowStateInfo {
    title: string;
    width: number;
    height: number;
    left: number;
    top: number;
    state: 'Normal' | 'Minimized' | 'Maximized' | string;
    topMost: boolean;
    opacity: number;
  }

  export interface Engine {
    log(text: unknown): void;
    mod: { readonly name: string; readonly version: string; options(): Record<string, Json> };
    app: {
      readonly version: string;
      readonly platform: 'windows' | 'linux' | 'macos';
      texts(): Record<string, string>;
      setText(target: string, text: string): string;
      clearText(target: string): boolean;
      background(): Json;
      setBackground(request: string | { file: string; gradientOpacity?: number; blackOverlayOpacity?: number; displayEverywhere?: boolean }): string;
      clearBackground(): void;
    };
    ui: {
      resolve(e?: unknown): { [K in ElementName]: K };
      open(request: { id: string; title?: string }): { isPlaced: boolean };
      close(id: string): void;
      openBanner(request: string | { id: string; position?: 'top' | 'bottom' }): { isPlaced: boolean; position: string };
      closeBanner(id: string): void;
      attach(request: { id: string; anchor: string; placement?: 'before' | 'after' | 'start' | 'end' }): { isPlaced: boolean; reason: string };
      detach(id: string): void;
      anchors(): Anchor[];
      tree(request?: { root?: UiTarget; depth?: number; namedOnly?: boolean }): UiNode;
      query(target: UiTarget): UiNode[];
      rule(request: { id: string; target: UiTarget; set: UiProperties }): UiChangeResult;
      move(request: { id: string; target: UiTarget; into: UiTarget; index?: number }): UiChangeResult;
      resources(): { key: string; value: string }[];
      setResources(values: Record<string, string>): UiChangeResult;
      redesign(spec: UiRedesign): UiChangeResult;
      undo(id: string): void;
      reset(): void;
      changes(): { suspended: boolean; rules: Json[]; moves: Json[]; resources: Record<string, string> };
      openDialog(request: string | { id: string; title?: string; width?: number; height?: number }): void;
      closeDialog(id: string): void;
      message(text: string, title?: string): void;
      confirm(text: string, title?: string): boolean;
      status(text?: string): void;
      toast(title: string, text?: string): void;
      refresh(): void;
    };
    settings: {
      list(): Record<string, Json>;
      get(name: string): Json | undefined;
      set(name: string, value: Json): void;
    };
    fastFlags: {
      getAll(): Record<string, string>;
      get(name: string): string | undefined;
      set(name: string, value: string | number | boolean | null): void;
      remove(name: string): void;
      clear(): void;
    };
    store: { get(key: string): Json | undefined; set(key: string, value: Json): void; remove(key: string): void; keys(): string[] };
    state: { get(key: string): Json | undefined; set(key: string, value: Json): void };
    clock: { now(): number; after(ms: number, fn: () => void | Promise<void>): number; every(ms: number, fn: () => void | Promise<void>): number; cancel(id: number): void };
    net: { fetch(request: string | FetchRequest): Promise<FetchResult> };
    sidebar: {
      get(): SidebarLayout;
      set(layout: { order?: string[]; hidden?: string[]; names?: Record<string, string>; icons?: Record<string, string> }): void;
      reset(): void;
      addPage(page: SidebarPageSpec): { key: string; id: string; title: string; section: string; icon: string };
      removePage(id: string): void;
    };
    modpages: {
      add(page: ModPageSpec): ModPage;
      remove(id: string): void;
      list(): ModPage[];
    };
    nav: {
      current(): string;
      pages(): string[];
      open(page: string): void;
    };
    theme: {
      get(): ThemeState;
      set(spec: ThemeUpdate): void;
      getBrush(key: string): string | undefined;
      setBrush(key: string, hexColor: string): void;
      resetBrushes(): void;
      listBrushes(): Record<string, string>;
    };
    window: {
      get(): WindowStateInfo;
      set(spec: Partial<WindowStateInfo>): void;
    };
    roblox: {
      status(): { isRunning: boolean; pid: number };
      launch(spec?: { mode?: 'player' | 'studio'; placeId?: number; jobId?: string }): void;
      request(request: string | { url: string; method?: 'GET' | 'POST'; body?: string }): Promise<FetchResult>;
      account(): Promise<RobloxAccountInfo>;
    };
    clipboard: {
      get(): string;
      set(text: string): void;
    };
    cursor: {
      get(): CursorPosition;
    };
  }

  export interface Matchers {
    toBe(expected: unknown): void;
    toEqual(expected: unknown): void;
    toBeTruthy(): void;
    toBeFalsy(): void;
    toBeDefined(): void;
    toBeNull(): void;
    toBeGreaterThan(expected: number): void;
    toBeLessThan(expected: number): void;
    toContain(expected: unknown): void;
    toThrow(): void;
  }

  export interface Atom<T> {
    readonly ref: string;
    readonly initial: T;
  }
}

declare module 'voidstrap/testing' {
  import type { EventName, Events, Node } from 'voidstrap';
  export const test: (name: string, body: () => void | Promise<void>) => void;
  export const expect: (actual: unknown) => import('voidstrap').Matchers & { not: import('voidstrap').Matchers };
  export const mod: {
    emit<E extends EventName>(event: E, e: Events[E]['input']): Events[E]['result'];
    render(paneId: string): Node | null;
    renderPage(pageId: string): Node | null;
    renderBanner(bannerId: string): Node | null;
    renderDialog(dialogId: string): Node | null;
    renderWidget(widgetId: string): Node | null;
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
declare function expect(actual: unknown): import('voidstrap').Matchers & { not: import('voidstrap').Matchers };
declare const mod: {
  emit<E extends import('voidstrap').EventName>(event: E, e: import('voidstrap').Events[E]['input']): import('voidstrap').Events[E]['result'];
  render(paneId: string): import('voidstrap').Node | null;
  renderPage(pageId: string): import('voidstrap').Node | null;
  renderBanner(bannerId: string): import('voidstrap').Node | null;
  renderDialog(dialogId: string): import('voidstrap').Node | null;
  renderWidget(widgetId: string): import('voidstrap').Node | null;
  press(tree: import('voidstrap').Node | null, key: string, value?: unknown): void;
};

declare namespace JSX {
  type Element = import('voidstrap').Node;
  type VoidstrapElements = { [K in keyof import('voidstrap').ElementProps]: import('voidstrap').ElementProps[K] & { children?: unknown } };
  interface IntrinsicElements extends VoidstrapElements {}
  interface ElementChildrenAttribute { children: {} }
}
