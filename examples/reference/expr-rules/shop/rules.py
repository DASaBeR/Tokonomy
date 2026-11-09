"""Safe expression evaluator for discount rules."""

import re
from decimal import Context, Decimal, ROUND_HALF_EVEN, ROUND_HALF_UP

_CTX = Context(prec=28, rounding=ROUND_HALF_EVEN)
_ROUND_CTX = Context(prec=2500, rounding=ROUND_HALF_UP)
_KEYWORDS = {"or", "and", "not", "true", "false"}
_CMP = {"==", "!=", "<", "<=", ">", ">="}
_ARITY = {"min": (1, None), "max": (1, None), "abs": (1, 1), "round": (1, 2), "if": (3, 3)}
_TOKEN = re.compile(
    r"(?P<ws>[ \t\r\n]+)"
    r"|(?P<num>[0-9]+(?:\.[0-9]+)?|\.[0-9]+)"
    r"|(?P<id>[A-Za-z_][A-Za-z0-9_]*)"
    r"|(?P<op>\*\*|==|!=|<=|>=|[<>+\-*/%])"
    r"|(?P<punct>[(),])"
)


class RulesError(ValueError):
    def __init__(self, message, column, code):
        super().__init__(f"{message} (column {column})")
        self.column = column
        self.code = code


def _tokenize(expr):
    toks = []
    pos = 0
    n = len(expr)
    while pos < n:
        col = pos + 1
        if expr[pos] == "'":
            i = pos + 1
            buf = []
            while True:
                if i >= n:
                    toks.append(("err", None, col))
                    toks.append(("end", None, n + 1))
                    return toks
                if expr[i] == "'":
                    if i + 1 < n and expr[i + 1] == "'":
                        buf.append("'")
                        i += 2
                        continue
                    break
                buf.append(expr[i])
                i += 1
            toks.append(("str", "".join(buf), col))
            pos = i + 1
            continue
        m = _TOKEN.match(expr, pos)
        if not m:
            toks.append(("err", expr[pos], col))
            toks.append(("end", None, n + 1))
            return toks
        kind = m.lastgroup
        text = m.group()
        pos = m.end()
        if kind == "ws":
            continue
        if kind == "id" and text in _KEYWORDS:
            kind = "kw"
        toks.append((kind, text, col))
    toks.append(("end", None, n + 1))
    return toks


class _Parser:
    def __init__(self, expr):
        self.toks = _tokenize(expr)
        self.i = 0
        self.static = None  # (column, code, message)

    @property
    def tok(self):
        return self.toks[self.i]

    def advance(self):
        t = self.toks[self.i]
        if t[0] != "end":
            self.i += 1
        return t

    def fail(self):
        raise RulesError("syntax error", self.tok[2], "syntax")

    def is_(self, kind, text=None):
        t = self.tok
        return t[0] == kind and (text is None or t[1] == text)

    def parse(self):
        node = self.p_or()
        if self.tok[0] != "end":
            self.fail()
        if self.static:
            col, code, msg = self.static
            raise RulesError(msg, col, code)
        return node

    def p_or(self):
        left = self.p_and()
        while self.is_("kw", "or"):
            col = self.advance()[2]
            left = ("or", col, left, self.p_and())
        return left

    def p_and(self):
        left = self.p_not()
        while self.is_("kw", "and"):
            col = self.advance()[2]
            left = ("and", col, left, self.p_not())
        return left

    def p_not(self):
        if self.is_("kw", "not"):
            col = self.advance()[2]
            return ("not", col, self.p_not())
        return self.p_cmp()

    def p_cmp(self):
        left = self.p_add()
        if self.tok[0] == "op" and self.tok[1] in _CMP:
            t = self.advance()
            return ("bin", t[1], t[2], left, self.p_add())
        return left

    def p_add(self):
        left = self.p_mul()
        while self.tok[0] == "op" and self.tok[1] in ("+", "-"):
            t = self.advance()
            left = ("bin", t[1], t[2], left, self.p_mul())
        return left

    def p_mul(self):
        left = self.p_unary()
        while self.tok[0] == "op" and self.tok[1] in ("*", "/", "%"):
            t = self.advance()
            left = ("bin", t[1], t[2], left, self.p_unary())
        return left

    def p_unary(self):
        if self.tok[0] == "op" and self.tok[1] in ("+", "-"):
            t = self.advance()
            return ("neg" if t[1] == "-" else "pos", t[2], self.p_unary())
        return self.p_power()

    def p_power(self):
        base = self.p_atom()
        if self.is_("op", "**"):
            t = self.advance()
            return ("bin", "**", t[2], base, self.p_unary())
        return base

    def note_static(self, col, code, msg):
        if self.static is None or col < self.static[0]:
            self.static = (col, code, msg)

    def p_atom(self):
        t = self.tok
        kind = t[0]
        if kind == "num":
            self.advance()
            return ("lit", Decimal(t[1]))
        if kind == "str":
            self.advance()
            return ("lit", t[1])
        if kind == "kw" and t[1] in ("true", "false"):
            self.advance()
            return ("lit", t[1] == "true")
        if kind == "id":
            self.advance()
            if self.is_("punct", "("):
                return self.call(t)
            return ("var", t[1], t[2])
        if kind == "punct" and t[1] == "(":
            self.advance()
            node = self.p_or()
            if not self.is_("punct", ")"):
                self.fail()
            self.advance()
            return node
        self.fail()

    def call(self, name_tok):
        self.advance()  # (
        args = []
        if self.is_("punct", ")"):
            self.advance()
        else:
            while True:
                args.append(self.p_or())
                if self.is_("punct", ","):
                    self.advance()
                elif self.is_("punct", ")"):
                    self.advance()
                    break
                else:
                    self.fail()
        name, col = name_tok[1], name_tok[2]
        if name not in _ARITY:
            self.note_static(col, "unknown_function", f"unknown function {name!r}")
        else:
            lo, hi = _ARITY[name]
            if len(args) < lo or (hi is not None and len(args) > hi):
                self.note_static(col, "arity", f"wrong number of arguments for {name!r}")
        return ("call", name, col, args)


def _is_num(v):
    return isinstance(v, Decimal)


def _err(col, code, msg):
    raise RulesError(msg, col, code)


def _is_int(d):
    return d == d.to_integral_value()


def _eval(node, env):
    k = node[0]
    if k == "lit":
        return node[1]
    if k == "var":
        name, col = node[1], node[2]
        if name not in env:
            _err(col, "unknown_variable", f"unknown variable {name!r}")
        v = env[name]
        if isinstance(v, (bool, str, Decimal)):
            return v
        if isinstance(v, int):
            return Decimal(v)
        _err(col, "type", f"unsupported value for {name!r}")
    if k in ("neg", "pos"):
        v = _eval(node[2], env)
        if not _is_num(v):
            _err(node[1], "type", "unary operator needs a number")
        return _CTX.minus(v) if k == "neg" else _CTX.plus(v)
    if k == "not":
        v = _eval(node[2], env)
        if not isinstance(v, bool):
            _err(node[1], "type", "'not' needs a boolean")
        return not v
    if k in ("and", "or"):
        left = _eval(node[2], env)
        if not isinstance(left, bool):
            _err(node[1], "type", f"'{k}' needs booleans")
        if (k == "and" and not left) or (k == "or" and left):
            return left
        right = _eval(node[3], env)
        if not isinstance(right, bool):
            _err(node[1], "type", f"'{k}' needs booleans")
        return right
    if k == "bin":
        op, col = node[1], node[2]
        a = _eval(node[3], env)
        b = _eval(node[4], env)
        if op in ("==", "!="):
            same = (
                (_is_num(a) and _is_num(b))
                or (isinstance(a, bool) and isinstance(b, bool))
                or (isinstance(a, str) and isinstance(b, str))
            )
            if not same:
                _err(col, "type", "cannot compare these types")
            return (a == b) if op == "==" else (a != b)
        if not (_is_num(a) and _is_num(b)):
            _err(col, "type", f"operator {op!r} needs numbers")
        if op == "<":
            return a < b
        if op == "<=":
            return a <= b
        if op == ">":
            return a > b
        if op == ">=":
            return a >= b
        if op == "+":
            return _CTX.add(a, b)
        if op == "-":
            return _CTX.subtract(a, b)
        if op == "*":
            return _CTX.multiply(a, b)
        if op == "/":
            if b == 0:
                _err(col, "division_by_zero", "division by zero")
            return _CTX.divide(a, b)
        if op == "%":
            if b == 0:
                _err(col, "division_by_zero", "modulo by zero")
            r = _CTX.remainder(a, b)
            if r != 0 and (r < 0) != (b < 0):
                r = _CTX.add(r, b)
            return r
        if op == "**":
            if not _is_int(b) or abs(b) > 1000:
                _err(col, "type", "exponent must be an integer with magnitude <= 1000")
            if a == 0:
                if b < 0:
                    _err(col, "division_by_zero", "zero to a negative power")
                if b == 0:
                    return Decimal(1)
            return _CTX.power(a, b)
    if k == "call":
        return _call(node, env)
    raise AssertionError(node)


def _call(node, env):
    _, name, col, args = node
    if name == "if":
        cond = _eval(args[0], env)
        if not isinstance(cond, bool):
            _err(col, "type", "if() condition must be a boolean")
        return _eval(args[1] if cond else args[2], env)
    vals = [_eval(a, env) for a in args]
    if not all(_is_num(v) for v in vals):
        _err(col, "type", f"{name}() needs numbers")
    if name == "min":
        return min(vals)
    if name == "max":
        return max(vals)
    if name == "abs":
        return _CTX.abs(vals[0])
    x = vals[0]
    n = vals[1] if len(vals) > 1 else Decimal(0)
    if not _is_int(n) or abs(n) > 1000:
        _err(col, "type", "round() digits must be an integer of magnitude <= 1000")
    n = int(n)
    return x.quantize(Decimal(1).scaleb(-n), rounding=ROUND_HALF_UP, context=_ROUND_CTX)


def evaluate(expr, variables=None):
    tree = _Parser(expr).parse()
    first = _tokenize(expr)[0]
    result = _eval(tree, dict(variables or {}))
    if isinstance(result, str):
        _err(first[2], "type", "expression result must be a number or boolean")
    return result
