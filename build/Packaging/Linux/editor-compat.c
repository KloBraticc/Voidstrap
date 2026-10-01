void *ImmGetContext(void *window) { return 0; }
int ImmReleaseContext(void *window, void *context) { return 1; }
void *ImmGetDefaultIMEWnd(void *window) { return 0; }
int ImmSetCompositionWindow(void *context, void *form) { return 1; }
int ImmSetCompositionFont(void *context, void *font) { return 1; }
int ImmSetCompositionFontW(void *context, void *font) { return 1; }
int ImmGetCompositionFont(void *context, void *font) { return 0; }
int ImmGetCompositionFontW(void *context, void *font) { return 0; }
int ImmNotifyIME(void *context, unsigned int action, unsigned int index, unsigned int value) { return 1; }
void *ImmAssociateContext(void *window, void *context) { return 0; }
unsigned int GetCaretBlinkTime(void) { return 530; }
int CreateCaret(void *window, void *bitmap, int width, int height) { return 1; }
int DestroyCaret(void) { return 1; }
int SetCaretPos(int x, int y) { return 1; }
int ShowCaret(void *window) { return 1; }
int HideCaret(void *window) { return 1; }
void *GetFocus(void) { return 0; }
void *SetFocus(void *window) { return 0; }
void *GetWindow(void *window, unsigned int command) { return 0; }
