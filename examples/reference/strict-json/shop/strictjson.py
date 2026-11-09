"""Strict hand-written JSON parser."""

MAX_DEPTH = 64
_HEX = "0123456789abcdefABCDEF"
_WS = " \t\n\r"
_ESC = {'"': '"', "\\": "\\", "/": "/", "b": "\b", "f": "\f", "n": "\n", "r": "\r", "t": "\t"}


class StrictJSONError(ValueError):
    def __init__(self, code, line, column):
        super().__init__("%s at line %d column %d" % (code, line, column))
        self.code = code
        self.line = line
        self.column = column


class _Parser:
    def __init__(self, text):
        self.s = text
        self.n = len(text)
        self.i = 0

    def err(self, code, index):
        s = self.s
        line = s.count("\n", 0, index) + 1
        column = index - (s.rfind("\n", 0, index) + 1) + 1
        raise StrictJSONError(code, line, column)

    def ws(self):
        while self.i < self.n and self.s[self.i] in _WS:
            self.i += 1

    def bad(self):
        """Error for the character at self.i (or end of input)."""
        self.err("unexpected_end" if self.i >= self.n else "unexpected_char", self.i)

    def parse(self):
        if self.s.startswith(chr(0xFEFF)):
            self.err("bom", 0)
        self.ws()
        if self.i >= self.n:
            self.err("empty", self.n)
        value = self.value(0)
        self.ws()
        if self.i < self.n:
            self.err("trailing_data", self.i)
        return value

    def value(self, depth):
        if self.i >= self.n:
            self.bad()
        c = self.s[self.i]
        if c == '"':
            return self.string()
        if c == "[":
            return self.array(depth + 1)
        if c == "{":
            return self.obj(depth + 1)
        if c == "-" or "0" <= c <= "9":
            return self.number()
        if c == "t":
            return self.literal("true", True)
        if c == "f":
            return self.literal("false", False)
        if c == "n":
            return self.literal("null", None)
        self.bad()

    def literal(self, word, result):
        for k, ch in enumerate(word):
            if self.i + k >= self.n:
                self.err("unexpected_end", self.n)
            if self.s[self.i + k] != ch:
                self.err("unexpected_char", self.i + k)
        self.i += len(word)
        return result

    def digits(self):
        start = self.i
        while self.i < self.n and "0" <= self.s[self.i] <= "9":
            self.i += 1
        if self.i == start:
            self.bad()

    def number(self):
        s = self.s
        start = self.i
        is_float = False
        if s[self.i] == "-":
            self.i += 1
        if self.i < self.n and s[self.i] == "0":
            self.i += 1
        else:
            self.digits()
        if self.i < self.n and s[self.i] == ".":
            is_float = True
            self.i += 1
            self.digits()
        if self.i < self.n and s[self.i] in "eE":
            is_float = True
            self.i += 1
            if self.i < self.n and s[self.i] in "+-":
                self.i += 1
            self.digits()
        text = s[start:self.i]
        return float(text) if is_float else int(text)

    def array(self, depth):
        if depth > MAX_DEPTH:
            self.err("too_deep", self.i)
        self.i += 1
        self.ws()
        if self.i >= self.n:
            self.bad()
        out = []
        if self.s[self.i] == "]":
            self.i += 1
            return out
        while True:
            out.append(self.value(depth))
            self.ws()
            if self.i >= self.n:
                self.bad()
            c = self.s[self.i]
            if c == "]":
                self.i += 1
                return out
            if c != ",":
                self.bad()
            self.i += 1
            self.ws()

    def obj(self, depth):
        if depth > MAX_DEPTH:
            self.err("too_deep", self.i)
        self.i += 1
        self.ws()
        if self.i >= self.n:
            self.bad()
        out = {}
        if self.s[self.i] == "}":
            self.i += 1
            return out
        while True:
            if self.i >= self.n or self.s[self.i] != '"':
                self.bad()
            start = self.i
            key = self.string()
            if key in out:
                self.err("duplicate_key", start)
            self.ws()
            if self.i >= self.n or self.s[self.i] != ":":
                self.bad()
            self.i += 1
            self.ws()
            out[key] = self.value(depth)
            self.ws()
            if self.i >= self.n:
                self.bad()
            c = self.s[self.i]
            if c == "}":
                self.i += 1
                return out
            if c != ",":
                self.bad()
            self.i += 1
            self.ws()

    def hex4(self):
        v = 0
        for _ in range(4):
            if self.i >= self.n or self.s[self.i] not in _HEX:
                self.bad()
            v = v * 16 + int(self.s[self.i], 16)
            self.i += 1
        return v

    def string(self):
        s = self.s
        self.i += 1
        buf = []
        while True:
            if self.i >= self.n:
                self.err("unexpected_end", self.n)
            c = s[self.i]
            if c == '"':
                self.i += 1
                return "".join(buf)
            if c == "\\":
                bs = self.i
                self.i += 1
                if self.i >= self.n:
                    self.err("unexpected_end", self.n)
                e = s[self.i]
                if e in _ESC:
                    buf.append(_ESC[e])
                    self.i += 1
                elif e == "u":
                    self.i += 1
                    cp = self.hex4()
                    if 0xDC00 <= cp <= 0xDFFF:
                        self.err("bad_surrogate", bs)
                    if 0xD800 <= cp <= 0xDBFF:
                        if s[self.i:self.i + 2] != "\\u":
                            self.err("bad_surrogate", bs)
                        self.i += 2
                        lo = self.hex4()
                        if not 0xDC00 <= lo <= 0xDFFF:
                            self.err("bad_surrogate", bs)
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00)
                    buf.append(chr(cp))
                else:
                    self.err("unexpected_char", self.i)
            elif ord(c) < 0x20:
                self.err("control_char", self.i)
            else:
                buf.append(c)
                self.i += 1


def loads(text):
    if not isinstance(text, str):
        raise TypeError("loads() requires str, got %s" % type(text).__name__)
    return _Parser(text).parse()
