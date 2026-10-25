use voidstate::encode;

#[test]
fn accepts_tolerant_json() {
    assert!(encode(b"\xEF\xBB\xBF{ // c\n \"a\": [1, 2,], /* x */ \"b\": \"\\u00e9\\n\\ud83d\\ude00\", }").is_some());
}

#[test]
fn rejects_broken_json() {
    let cases: [&[u8]; 9] = [b"{", b"{\"a\":}", b"{\"a\":01}", b"[1 2]", b"\"\\ud800\"", b"{} x", b"/* open", b"{\"a\":tru}", b"{\"a\":1.}"];
    for bad in cases {
        assert!(encode(bad).is_none(), "{:?}", std::str::from_utf8(bad));
    }
}

#[test]
fn rejects_deep_nesting() {
    let deep = "[".repeat(100) + &"]".repeat(100);
    assert!(encode(deep.as_bytes()).is_none());
}

#[test]
fn rejects_invalid_utf8() {
    assert!(encode(b"{\"a\":\"\xff\"}").is_none());
}
