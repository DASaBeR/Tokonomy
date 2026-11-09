"""Hidden acceptance test: copied in only after the run, never shown to workers."""

import unittest
from decimal import Decimal

from shop.rules import RulesError, evaluate

D = Decimal


class RulesBase(unittest.TestCase):
    def num(self, expr, expected, variables=None):
        with self.subTest(expr=expr):
            got = evaluate(expr, variables)
            self.assertIsInstance(got, Decimal)
            self.assertNotIsInstance(got, bool)
            self.assertEqual(got, D(expected))

    def boolean(self, expr, expected, variables=None):
        with self.subTest(expr=expr):
            self.assertIs(evaluate(expr, variables), expected)

    def fails(self, expr, code, column, variables=None):
        with self.subTest(expr=expr):
            with self.assertRaises(RulesError) as cm:
                evaluate(expr, variables)
            self.assertEqual((cm.exception.code, cm.exception.column), (code, column))


class Arithmetic(RulesBase):
    def test_numbers_and_literals(self):
        for expr, exp in [("12", "12"), ("12.50", "12.5"), (".5", "0.5"), ("007", "7"), ("  3  ", "3"),
                          ("\t1\r\n+\n2", "3"), ("+5", "5"), ("--2", "2"), ("2 - -3", "5"), ("2--3", "5"),
                          ("-(2+3)", "-5")]:
            self.num(expr, exp)

    def test_precedence_and_left_associativity(self):
        for expr, exp in [("1 + 2 * 3", "7"), ("(1 + 2) * 3", "9"), ("10 - 3 - 2", "5"), ("100 / 10 / 5", "2"),
                          ("8 / 2 * 4", "16"), ("2 * 3 % 4", "2"), ("20 % 7 * 2", "12"), ("2 + 3 * 4 - 5", "9"),
                          ("-3 * 2", "-6"), ("-2 * -3", "6"), ("2 * 3 ** 2", "18"), ("2 ** 3 * 2", "16"),
                          ("10 - 2 ** 2", "6"), ("10 % 4 + 1", "3")]:
            self.num(expr, exp)

    def test_power(self):
        for expr, exp in [("-2 ** 2", "-4"), ("(-2) ** 2", "4"), ("2 ** 3 ** 2", "512"),
                          ("(2 ** 3) ** 2", "64"), ("2 ** -1", "0.5"), ("2 ** -2", "0.25"),
                          ("-2 ** -2", "-0.25"), ("2 ** -3 ** 2", str(D(2) ** D(-9))), ("-8 ** 3", "-512"),
                          ("(-8) ** 3", "-512"), ("0.5 ** 2", "0.25"), ("2 ** 10", "1024"), ("2 ** 2.0", "4"),
                          ("10 ** -2", "0.01"), ("0 ** 0", "1"), ("0 ** 5", "0"), ("5 ** 0", "1"),
                          ("--2 ** 2", "4")]:
            self.num(expr, exp)

    def test_modulo_has_sign_of_divisor(self):
        for expr, exp in [("7 % 3", "1"), ("-7 % 3", "2"), ("7 % -3", "-2"), ("-7 % -3", "-1"),
                          ("7.5 % 2", "1.5"), ("-7.5 % 2", "0.5"), ("5.5 % -2", "-0.5"), ("6 % -3", "0"),
                          ("-6 % 3", "0"), ("0.3 % 0.1", "0"), ("1 % 0.3", "0.1"), ("0 % 5", "0")]:
            self.num(expr, exp)

    def test_decimal_exactness_and_precision(self):
        self.boolean("0.1 + 0.2 == 0.3", True)
        self.boolean("0.1 * 3 == 0.3", True)
        self.boolean("1.1 * 1.1 == 1.21", True)
        self.boolean("1 / 3 * 3 == 1", False)
        self.boolean("1 / 3 + 1 / 3 + 1 / 3 == 1", False)
        self.num("1 / 3", "0.3333333333333333333333333333")
        self.num("2 / 3", "0.6666666666666666666666666667")
        self.num("10 / 4", "2.5")
        self.num("1 / 8", "0.125")
        self.num("-7 / 2", "-3.5")
        self.num("12345678901234567890123456789 + 0", "12345678901234567890123456790")
        self.num("2 ** 100", "1.267650600228229401496703205E+30")
        self.num("0.5 / 0.25", "2")

    def test_variables(self):
        v = {"price": D("19.99"), "qty": 3, "pct": D("0.15"), "name": "bob", "flag": True}
        self.num("price * qty", "59.97", v)
        self.num("qty", "3", v)
        self.num("qty * 2 + 1", "7", v)
        self.boolean("flag", True, v)
        self.boolean("flag and qty > 2", True, v)
        self.boolean("name == 'bob'", True, v)
        self.num("round(price * qty * pct, 2)", "9.00", v)
        self.num("_y1 + a_b", "5", {"_y1": 2, "a_b": 3})

    def test_keyword_prefixed_identifiers(self):
        v = {"android": 2, "order": 3, "notify": 1, "trueish": 4, "nothing": 5}
        self.num("android + order + notify + trueish + nothing", "15", v)
        self.boolean("(1<2)and(2<3)", True)
        self.boolean("not(1>2)or false", True)

    def test_variable_names_are_not_functions_and_vice_versa(self):
        self.num("max + 1", "8", {"max": 7})
        self.num("max(1, max)", "9", {"max": 9})
        self.num("max (1, 2)", "2")
        self.fails("max + 1", "unknown_variable", 1)

    def test_variables_not_mutated_and_default(self):
        v = {"x": 1}
        evaluate("x + 1", v)
        self.assertEqual(v, {"x": 1})
        self.num("1 + 1", "2", None)


class Logic(RulesBase):
    def test_precedence_table(self):
        for expr, exp in [("true or false and false", True), ("false and false or true", True),
                          ("not true or true", True), ("not false and false", False),
                          ("not true and true", False), ("not not true", True), ("not (true or true)", False),
                          ("true and not false", True), ("1 + 2 == 3 and 2 * 3 > 5", True),
                          ("1 + 2 * 3 == 7", True), ("-1 < 0", True), ("2 ** 3 == 8", True),
                          ("10 % 4 == 2", True), ("1 != 1.0", False), ("3 >= 3", True), ("3 <= 2", False),
                          ("0.50 == .5", True)]:
            self.boolean(expr, exp)

    def test_parenthesised_bool_comparison(self):
        self.boolean("(1 < 2) == true", True)
        self.boolean("(1 < 2) != (2 < 1)", True)
        self.boolean("true == (1 == 1)", True)

    def test_short_circuit(self):
        self.boolean("x != 0 and 10 / x > 1", False, {"x": 0})
        self.boolean("x != 0 and 10 / x > 1", True, {"x": 5})
        self.boolean("x == 0 or 10 / x > 1", True, {"x": 0})
        self.boolean("x == 0 or 10 / x > 1", False, {"x": 20})
        self.boolean("false and y", False)
        self.boolean("true or y", True)
        self.boolean("false and 1", False)
        self.boolean("true or 1", True)
        self.boolean("false and (1 + 'a' == 2)", False)
        self.boolean("true or not 5", True)

    def test_and_or_need_bools(self):
        self.fails("true and 1", "type", 6)
        self.fails("1 and true", "type", 3)
        self.fails("1 and y", "type", 3)
        self.fails("false or 1", "type", 7)
        self.fails("1 or true", "type", 3)
        self.fails("true and 'a'", "type", 6)

    def test_not(self):
        self.fails("not 1", "type", 1)
        self.fails("true and not 'a'", "type", 10)
        self.fails("not", "syntax", 4)
        self.fails("1 == not 2", "syntax", 6)
        self.fails("1 + not true", "syntax", 5)

    def test_if_is_lazy(self):
        self.num("if(1 < 2, 10, 1 / 0)", "10")
        self.num("if(1 > 2, 1 / 0, 5)", "5")
        self.num("if(true, 1, y)", "1")
        self.num("if(false, 'a' + 1, 2)", "2")
        self.boolean("if(true, 'a', 'b') == 'a'", True)
        self.boolean("if(false, 'a', 'b') == 'a'", False)
        self.num("if(x > 100, 1, if(x > 10, 2, 3))", "2", {"x": 50})
        self.boolean("if(true, false, true)", False)
        self.num("if(true, 1, 2) + if(false, 10, 20)", "21")
        self.num("round(if(total >= 100 and member, total * 0.15, 0), 2)", "15.75",
                 {"total": D("105"), "member": True})

    def test_if_errors(self):
        self.fails("if(1, 2, 3)", "type", 1)
        self.fails("1 + if('a', 2, 3)", "type", 5)
        self.fails("if(x, 1, 2)", "unknown_variable", 4)
        self.fails("if(true, 1/0, 2)", "division_by_zero", 11)
        self.fails("if(false, 1, 2/0)", "division_by_zero", 15)


class Functions(RulesBase):
    def test_min_max_abs(self):
        for expr, exp in [("min(3, 1, 2)", "1"), ("max(3, 1, 2)", "3"), ("max(1)", "1"), ("min(5)", "5"),
                          ("max(1, 2.5, -1)", "2.5"), ("min(-1, -1.5)", "-1.5"), ("abs(-3)", "3"),
                          ("abs(-0.5)", "0.5"), ("abs(2 - 5)", "3"), ("abs(4)", "4"),
                          ("max(1, min(5, 3), 2) + 1", "4")]:
            self.num(expr, exp)

    def test_round_half_up_away_from_zero(self):
        for expr, exp in [("round(2.5)", "3"), ("round(-2.5)", "-3"), ("round(0.5)", "1"), ("round(-0.5)", "-1"),
                          ("round(1.5)", "2"), ("round(-1.5)", "-2"), ("round(2.4999)", "2"),
                          ("round(1.005, 2)", "1.01"), ("round(-1.005, 2)", "-1.01"),
                          ("round(2.675, 2)", "2.68"), ("round(0.049, 1)", "0"), ("round(-0.04, 1)", "0"),
                          ("round(1234, -2)", "1200"), ("round(1250, -2)", "1300"), ("round(-1250, -2)", "-1300"),
                          ("round(1249.99, -2)", "1200"), ("round(5, -1)", "10"), ("round(15, -1)", "20"),
                          ("round(7, 2)", "7"), ("round(3.14159, 3)", "3.142"), ("round(2.5, 0)", "3"),
                          ("round(2.5, 1.0)", "2.5"), ("round(1 / 3, 4)", "0.3333"),
                          ("round(12345678901234567890.5)", "12345678901234567891")]:
            self.num(expr, exp)

    def test_round_result_value_for_negative_digits(self):
        self.boolean("round(1234, -2) == 1200", True)
        self.boolean("round(0.125, 2) == 0.13", True)

    def test_unknown_function(self):
        self.fails("foo(1)", "unknown_function", 1)
        self.fails("1 + foo(2)", "unknown_function", 5)
        self.fails("Max(1, 2)", "unknown_function", 1)
        self.fails("x(1)", "unknown_function", 1, {"x": 1})
        self.fails("foo()", "unknown_function", 1)
        self.fails("foo(1 / 0)", "unknown_function", 1)
        self.fails("false and foo(1)", "unknown_function", 11)
        self.fails("if(true, 1, foo(2))", "unknown_function", 13)

    def test_arity(self):
        self.fails("min()", "arity", 1)
        self.fails("max()", "arity", 1)
        self.fails("abs()", "arity", 1)
        self.fails("abs(1, 2)", "arity", 1)
        self.fails("round()", "arity", 1)
        self.fails("round(1, 2, 3)", "arity", 1)
        self.fails("if(true, 1)", "arity", 1)
        self.fails("if()", "arity", 1)
        self.fails("if(true, 1, 2, 3)", "arity", 1)
        self.fails("1 + max()", "arity", 5)
        self.fails("max(abs(), 1)", "arity", 5)
        self.fails("false and min()", "arity", 11)
        self.fails("if(true, 1, max())", "arity", 13)

    def test_leftmost_static_error_wins(self):
        self.fails("1 + foo(1) + min()", "unknown_function", 5)
        self.fails("foo(min())", "unknown_function", 1)
        self.fails("abs(foo(), 1)", "arity", 1)
        self.fails("min() + foo(1)", "arity", 1)
        self.fails("foo(1) + 1 / 0", "unknown_function", 1)
        self.fails("y + foo(1)", "unknown_function", 5)

    def test_function_type_errors(self):
        self.fails("min(1, true)", "type", 1)
        self.fails("max('a')", "type", 1)
        self.fails("abs(true)", "type", 1)
        self.fails("1 + abs('a')", "type", 5)
        self.fails("round('a')", "type", 1)
        self.fails("round(1.5, 1.5)", "type", 1)
        self.fails("round(1, true)", "type", 1)
        self.fails("round(1, 1001)", "type", 1)
        self.fails("max(1, 2 > 1)", "type", 1)

    def test_function_args_all_evaluated_before_type_check(self):
        self.fails("max('a', y)", "unknown_variable", 10)
        self.fails("max(1/0, 'a')", "division_by_zero", 6)
        self.fails("max('a', 1/0)", "division_by_zero", 11)


class Strings(RulesBase):
    def test_quote_escaping(self):
        self.boolean("name == 'it''s'", True, {"name": "it's"})
        self.boolean("'''' == q", True, {"q": "'"})
        self.boolean("'' == ''", True)
        self.boolean("'a''' == 'a'''", True)
        self.boolean("'a''b' == 'a'", False)
        self.boolean("'A' == 'a'", False)
        self.boolean("'a' != 'b'", True)
        self.boolean("'a < b (' == s", True, {"s": "a < b ("})
        self.boolean("s == ''", True, {"s": ""})
        self.boolean("'x' == 'x' and 'y' != 'z'", True)
        self.boolean("if(tier == 'gold', true, false)", True, {"tier": "gold"})

    def test_string_type_errors(self):
        self.fails("'a' < 'b'", "type", 5)
        self.fails("'a' >= 'b'", "type", 5)
        self.fails("'a' + 'b'", "type", 5)
        self.fails("'x' + 1", "type", 5)
        self.fails("-'a'", "type", 1)
        self.fails("'a' == 1", "type", 5)
        self.fails("1 != 'a'", "type", 3)
        self.fails("'a' == true", "type", 5)
        self.fails("s * 2", "type", 3, {"s": "ab"})
        self.fails("'abc'", "type", 1)
        self.fails("  'abc'", "type", 3)
        self.fails("if(true, 'a', 'b')", "type", 1)
        self.fails("s", "type", 1, {"s": "x"})

    def test_bad_variable_value_type(self):
        self.fails("x", "type", 1, {"x": 1.5})
        self.fails("1 + x", "type", 5, {"x": None})
        self.boolean("false and x", False, {"x": 1.5})


class TypeErrors(RulesBase):
    def test_bool_arithmetic(self):
        self.fails("true + 1", "type", 6)
        self.fails("(1 < 2) * 3", "type", 9)
        self.fails("1 < true", "type", 3)
        self.fails("true == 1", "type", 6)
        self.fails("true < false", "type", 6)
        self.fails("-true", "type", 1)
        self.fails("+true", "type", 1)
        self.fails("flag + 1", "type", 6, {"flag": True})
        self.fails("2 ** true", "type", 3)
        self.fails("1 % false", "type", 3)

    def test_power_type_and_zero_errors(self):
        self.fails("2 ** 0.5", "type", 3)
        self.fails("2 ** 1001", "type", 3)
        self.fails("2 ** -1001", "type", 3)
        self.fails("2 ** 2 ** -1", "type", 3)
        self.fails("0 ** -1", "division_by_zero", 3)

    def test_evaluation_order(self):
        self.fails("y + 'a'", "unknown_variable", 1)
        self.fails("'a' + y", "unknown_variable", 7)
        self.fails("1 + 'a' + y", "type", 3)
        self.fails("'a' * (1 / 0)", "division_by_zero", 10)
        self.fails("y / 0", "unknown_variable", 1)
        self.fails("1 / y", "unknown_variable", 5)
        self.fails("1 / 0 + y", "division_by_zero", 3)
        self.fails("true == 1 / 0", "division_by_zero", 11)


class ErrorColumns(RulesBase):
    def test_division_by_zero(self):
        for expr, col in [("1 / 0", 3), ("5 % 0", 3), ("1 / (2 - 2)", 3), ("0 / 0", 3), ("1 % 0.00", 3),
                          ("1 + 2 / 0", 7), ("  4 /0", 5), ("(1 + 1) % (1 - 1)", 9)]:
            self.fails(expr, "division_by_zero", col)
        self.fails("10 / x", "division_by_zero", 4, {"x": 0})

    def test_unknown_variable(self):
        self.fails("x", "unknown_variable", 1)
        self.fails("1 + foo_bar", "unknown_variable", 5)
        self.fails("  Y", "unknown_variable", 3)
        self.fails("x", "unknown_variable", 1, {"X": 1})
        self.fails("true and y", "unknown_variable", 10)
        self.fails("1 + max(2, zz)", "unknown_variable", 12)

    def test_syntax_end_of_input(self):
        self.fails("", "syntax", 1)
        self.fails("   ", "syntax", 4)
        self.fails("1 +", "syntax", 4)
        self.fails("1 +  ", "syntax", 6)
        self.fails("(1 + 2", "syntax", 7)
        self.fails("max(1, 2", "syntax", 9)
        self.fails("if(true, 1, 2", "syntax", 14)
        self.fails("1 and", "syntax", 6)
        self.fails("foo(", "syntax", 5)
        self.fails("-", "syntax", 2)
        self.fails("2 **", "syntax", 5)

    def test_syntax_unexpected_token(self):
        for expr, col in [("1 2", 3), ("* 2", 1), ("1 + * 2", 5), ("()", 2), (")", 1), ("1 + 2)", 6),
                          ("max(1,)", 7), ("max(1 2)", 7), ("max(,1)", 5), ("(1 + 2 3)", 8), ("and", 1),
                          ("true false", 6), ("1 <> 2", 4), ("2x", 2), ("1.5.2", 4), ("1e3", 2),
                          ("1 or or 2", 6), ("x y", 3), ("1 +* 2", 4), ("2 ** * 3", 6), ("max(1)(2)", 7),
                          ("1 2 $", 3), ("1, 2", 2), ("true(1)", 5)]:
            self.fails(expr, "syntax", col)

    def test_syntax_unknown_character(self):
        for expr, col in [("1 $ 2", 3), ("1 = 1", 3), ("1 ! 1", 3), ("1 !== 1", 5), ("1.", 2), ("1..2", 2),
                          ("1 + é", 5), ("1 + 2", 2), ("a & b", 3), ("1 + ١", 5),
                          ("é + 1", 1), ("1 +\n  $", 7), ("x.y", 2), ('1 + "a"', 5), ("[1]", 1)]:
            self.fails(expr, "syntax", col)

    def test_syntax_unterminated_string(self):
        self.fails("' unterminated", "syntax", 1)
        self.fails("1 + 'abc", "syntax", 5)
        self.fails("'a''", "syntax", 1)
        self.fails("'a' == 'b", "syntax", 8)

    def test_comparison_does_not_chain(self):
        self.fails("1 < 2 < 3", "syntax", 7)
        self.fails("1 == 1 == 1", "syntax", 8)
        self.fails("1 < 2 == true", "syntax", 7)
        self.fails("1 < 2 > 0", "syntax", 7)
        self.fails("(1 < 2 < 3)", "syntax", 8)
        self.fails("1 != 2 != 3", "syntax", 8)
        self.boolean("1 < 2 and 2 < 3", True)
        self.boolean("(1 < 2) == true", True)

    def test_syntax_beats_everything_else(self):
        self.fails("a b", "syntax", 3)
        self.fails("x +", "syntax", 4)
        self.fails("1 / 0 $", "syntax", 7)
        self.fails("foo(1) 2", "syntax", 8)
        self.fails("min() +", "syntax", 8)
        self.fails("1 + 'a' )", "syntax", 9)
        self.fails("1 / 0 2", "syntax", 7)

    def test_error_is_value_error_with_attributes(self):
        with self.assertRaises(ValueError) as cm:
            evaluate("1 +")
        self.assertIsInstance(cm.exception, RulesError)
        self.assertEqual(cm.exception.code, "syntax")
        self.assertEqual(cm.exception.column, 4)


if __name__ == "__main__":
    unittest.main()
