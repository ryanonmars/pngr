"""Execute release eligibility guards without credentials or GitHub access."""
import importlib.util
import os
from pathlib import Path
import textwrap
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
BUILD = (ROOT / '.github/workflows/build.yml').read_text()
RELEASE = (ROOT / '.github/workflows/release.yml').read_text()
SECRETS = (
    'PNGR_MACOS_CERTIFICATE_P12_BASE64', 'PNGR_MACOS_CERTIFICATE_PASSWORD',
    'PNGR_APPLE_NOTARY_KEY_P8_BASE64', 'PNGR_APPLE_NOTARY_KEY_ID',
    'PNGR_APPLE_NOTARY_ISSUER_ID',
)


def step(workflow, name):
    return workflow.split(f'      - name: {name}\n', 1)[1].split('\n      - ', 1)[0]


def script(workflow, name):
    return compile(textwrap.dedent(step(workflow, name).split('        run: |\n', 1)[1]),
                   'workflow-eligibility', 'exec')


class ReleaseSigningWorkflowTests(unittest.TestCase):
    def environment(self, **changes):
        environment = {
            'PNGR_REQUEST_RELEASE_SIGNING': 'true', 'PNGR_PACKAGE': 'true',
            'PNGR_EVENT': 'push', 'PNGR_REF': 'refs/tags/v0.8.0',
            'PNGR_VERSION': 'v0.8.0', 'PNGR_TAG': 'v0.8.0',
        }
        environment.update(changes)
        return environment

    def execute(self, workflow, name, **changes):
        with mock.patch.dict(os.environ, self.environment(**changes), clear=True):
            exec(script(workflow, name), {})

    def test_matching_release_and_prerelease_tags_are_accepted(self):
        for tag in ('v0.8.0', 'v0.8.0-rc.1'):
            with self.subTest(tag=tag):
                changes = dict(PNGR_REF=f'refs/tags/{tag}', PNGR_VERSION=tag, PNGR_TAG=tag)
                self.execute(BUILD, 'Validate release signing eligibility', **changes)
                self.execute(RELEASE, 'Validate release tag', **changes)

    def test_manual_pr_branch_and_version_mismatch_are_rejected(self):
        cases = (
            {'PNGR_EVENT': 'workflow_dispatch'}, {'PNGR_EVENT': 'pull_request'},
            {'PNGR_REF': 'refs/heads/main'}, {'PNGR_VERSION': 'v0.8.1'},
            {'PNGR_PACKAGE': 'false'},
            {'PNGR_REF': 'refs/tags/v08.0.0', 'PNGR_VERSION': 'v08.0.0'},
        )
        for changes in cases:
            with self.subTest(changes=changes), self.assertRaises(SystemExit):
                self.execute(BUILD, 'Validate release signing eligibility', **changes)

    def test_release_preflight_rejects_non_tag_and_invalid_tag_events(self):
        for changes in ({'PNGR_EVENT': 'workflow_dispatch'}, {'PNGR_REF': 'refs/heads/main'},
                        {'PNGR_TAG': 'vgarbage'}, {'PNGR_TAG': 'v01.2.3'}):
            with self.subTest(changes=changes), self.assertRaises(SystemExit):
                self.execute(RELEASE, 'Validate release tag', **changes)

    def test_manual_unsigned_packaging_remains_available(self):
        self.execute(BUILD, 'Validate release signing eligibility',
                     PNGR_EVENT='workflow_dispatch', PNGR_REF='refs/heads/main',
                     PNGR_REQUEST_RELEASE_SIGNING='false')

    def test_secrets_are_explicitly_forwarded_and_only_used_by_prepare(self):
        prepare = step(BUILD, 'Prepare isolated release signing credentials')
        self.assertIn("if: inputs.release_signing && github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v')", prepare)
        self.assertIn('PNGR_VERSION: ${{ inputs.version }}', prepare)
        self.assertIn('run: python3 scripts/macos/release-signing.py prepare', prepare)
        for name in SECRETS:
            expression = '${{ secrets.' + name + ' }}'
            self.assertEqual(1, BUILD.count(expression))
            self.assertIn(f'{name}: {expression}', prepare)
            self.assertEqual(1, RELEASE.count(f'{name}: {expression}'))
        self.assertNotIn('secrets: inherit', RELEASE)
        ci = (ROOT / '.github/workflows/ci.yml').read_text()
        self.assertNotIn('secrets:', ci)
        self.assertNotIn('release_signing:', ci)

    def test_failure_cannot_continue_packaging_or_bypass_cleanup(self):
        prepare = step(BUILD, 'Prepare isolated release signing credentials')
        assemble = step(BUILD, 'Assemble universal application, DMG and update feed')
        cleanup = step(BUILD, 'Remove temporary signing credentials')
        self.assertNotIn('continue-on-error', prepare)
        self.assertNotIn('if:', assemble)  # Default success() skips packaging after failed preparation.
        self.assertIn("if: always() && steps.signing.outcome != 'skipped' && steps.signing.outcome != ''", cleanup)
        self.assertIn('run: python3 scripts/macos/release-signing.py cleanup', cleanup)
        self.assertLess(BUILD.index('Remove temporary signing credentials'),
                        BUILD.index('Upload universal installer assets'))
        self.assertIn('release_signing: true', RELEASE)
        self.assertIn('needs: platforms', RELEASE)

    def test_credential_helper_independently_rejects_non_release_invocation(self):
        spec = importlib.util.spec_from_file_location('release_signing_workflow',
                                                     ROOT / 'scripts/macos/release-signing.py')
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)
        baseline = {'GITHUB_ACTIONS': 'true', 'GITHUB_EVENT_NAME': 'push',
                    'GITHUB_REF': 'refs/tags/v0.8.0', 'PNGR_VERSION': 'v0.8.0'}
        with mock.patch.dict(os.environ, baseline, clear=True):
            self.assertTrue(helper.release_event())
        for changes in ({'GITHUB_ACTIONS': 'false'}, {'GITHUB_EVENT_NAME': 'workflow_dispatch'},
                        {'GITHUB_EVENT_NAME': 'pull_request'}, {'GITHUB_REF': 'refs/heads/main'},
                        {'PNGR_VERSION': 'v0.8.1'}):
            with self.subTest(changes=changes), mock.patch.dict(os.environ, baseline | changes, clear=True):
                with mock.patch.object(helper, 'run') as command:
                    with self.assertRaisesRegex(RuntimeError, 'version-tag push'):
                        helper.prepare()
                    command.assert_not_called()


if __name__ == '__main__':
    unittest.main()
