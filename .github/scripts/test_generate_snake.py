import unittest
import xml.etree.ElementTree as ET
from datetime import date, timedelta

from generate_snake import calendar_start, render


class CalendarTests(unittest.TestCase):
    def test_calendar_crosses_year_and_keeps_real_counts(self):
        today = date(2026, 1, 1)
        start = calendar_start(today)
        self.assertEqual(start, date(2025, 12, 7))
        counts = {start + timedelta(days=i): 1 for i in range((today - start).days + 1)}
        counts[today] = 12
        for theme in ("dark", "light"):
            svg = render(counts, today, theme)
            root = ET.fromstring(svg)
            self.assertEqual(root.attrib["viewBox"], "0 0 840 200")
            self.assertIn(">32</text>", svg)
            self.assertEqual(svg.count('stroke-dasharray="4 5"'), 2)
            self.assertEqual(svg.count('class="snake"'), 7)
            self.assertIn('class="snake-head"', svg)
            self.assertIn('translate(483px,76px)', svg)
            self.assertIn('rotate(180deg)', svg)
            self.assertIn('rotate(90deg)', svg)
            self.assertIn('animation-delay:0.04s', svg)
            self.assertIn("prefers-reduced-motion:reduce", svg)
            self.assertIn('>1</text>', svg)

    def test_incomplete_calendar_is_rejected(self):
        with self.assertRaises(ValueError):
            render({}, date(2026, 11, 2), "dark")


if __name__ == "__main__":
    unittest.main()
