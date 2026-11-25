#!/usr/bin/env python3
# Copyright (c) 2026 Seoul National University
# SPDX-License-Identifier: MIT

"""The Fusion IL weaver must not process a test assembly in the production project config."""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion"


class FusionWeaveListTests(unittest.TestCase):
    def test_no_test_assembly_is_in_the_production_weave_list(self):
        weave = json.loads(CONFIG.read_text(encoding="utf-8"))["AssembliesToWeave"]
        self.assertTrue(weave, "the weave list is not empty")
        self.assertEqual([name for name in weave if "Tests" in name or "Test" == name[-4:]], [])

    def test_no_test_assembly_defines_a_networked_behaviour(self):
        # Nothing in a test assembly may need weaving: the two-process cloud test drives the product's own
        # FusionConnectionManager instead of a weaved harness object.
        offenders = []
        for path in (ROOT / "Assets/SpatialBlend/Framework/Test").rglob("*.cs"):
            text = path.read_text(encoding="utf-8", errors="replace")
            if ": NetworkBehaviour" in text or "[Rpc(" in text:
                offenders.append(str(path.relative_to(ROOT)))
        self.assertEqual(offenders, [])


if __name__ == "__main__":
    unittest.main()
