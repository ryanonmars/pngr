"""Controlled dispatch and environment authorization; no GitHub calls or real keys."""
import importlib.util
import os
from pathlib import Path
import subprocess
import unittest
from unittest import mock
import urllib.error

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('verification_policy', ROOT / 'scripts/macos/verification-policy.py')
POLICY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(POLICY)
SHA = 'a' * 40
WORKFLOW = (ROOT / '.github/workflows/verify-macos-signing.yml').read_text()


class VerificationPolicyTests(unittest.TestCase):
    def environment(self, **changes):
        values = {'GITHUB_ACTIONS': 'true', 'GITHUB_REPOSITORY': POLICY.REPOSITORY,
                  'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_REF': 'refs/heads/main',
                  'GITHUB_WORKFLOW_REF': POLICY.WORKFLOW, 'GITHUB_SHA': SHA,
                  'PNGR_SIGNING_ENVIRONMENT': POLICY.ENVIRONMENT}
        return values | changes

    def protections(self):
        return {'name': POLICY.ENVIRONMENT, 'can_admins_bypass': False,
                'protection_rules': [{'type': 'required_reviewers', 'reviewers': [{'type': 'User', 'reviewer': {'login': 'ryanonmars'}}]}],
                'deployment_branch_policy': {'custom_branch_policies': True, 'protected_branches': False}}

    def branches(self):
        return {'total_count': 1, 'branch_policies': [{'name': 'main', 'type': 'branch'}]}

    def test_only_canonical_main_dispatch_is_accepted(self):
        with mock.patch.dict(os.environ, self.environment(), clear=True):
            self.assertEqual(POLICY.context(), SHA)
        for changes in ({'GITHUB_ACTIONS': 'false'}, {'GITHUB_REPOSITORY': 'fork/pngr'},
                        {'GITHUB_EVENT_NAME': 'pull_request'}, {'GITHUB_EVENT_NAME': 'push'},
                        {'GITHUB_REF': 'refs/heads/feature'}, {'GITHUB_REF': 'refs/tags/main'},
                        {'GITHUB_WORKFLOW_REF': POLICY.WORKFLOW.replace('main', 'feature')},
                        {'PNGR_SIGNING_ENVIRONMENT': 'unprotected'}, {'GITHUB_SHA': 'invalid'}):
            with self.subTest(changes=changes), mock.patch.dict(os.environ, self.environment(**changes), clear=True):
                with self.assertRaises(RuntimeError):
                    POLICY.context()

    def test_requires_existing_reviewers_and_exact_branch_only_policy(self):
        POLICY.validate_protections(self.protections(), self.branches())
        for changes in ({'name': 'unprotected'}, {'protection_rules': []},
                        {'protection_rules': [{'type': 'required_reviewers', 'reviewers': []}]},
                        {'deployment_branch_policy': None}, {'can_admins_bypass': True}):
            with self.subTest(changes=changes), self.assertRaises(RuntimeError):
                POLICY.validate_protections(self.protections() | changes, self.branches())
        for branches in ({'total_count': 0, 'branch_policies': []},
                         {'total_count': 2, 'branch_policies': [{'name': 'main', 'type': 'branch'}]},
                         {'total_count': 1, 'branch_policies': [{'name': '*', 'type': 'branch'}]},
                         {'total_count': 1, 'branch_policies': [{'name': 'main', 'type': 'tag'}]}):
            with self.subTest(branches=branches), self.assertRaises(RuntimeError):
                POLICY.validate_protections(self.protections(), branches)

    def test_checkout_and_live_main_must_match_dispatch_sha(self):
        with mock.patch.dict(os.environ, self.environment(), clear=True), \
                mock.patch.object(POLICY.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, SHA + '\n')):
            for main in (SHA, 'b' * 40):
                with mock.patch.object(POLICY, 'get', side_effect=[self.protections(), self.branches(), {'sha': main}]):
                    if main == SHA:
                        POLICY.verify()
                    else:
                        with self.assertRaisesRegex(RuntimeError, 'Main changed'):
                            POLICY.verify()
        with mock.patch.dict(os.environ, self.environment(), clear=True), \
                mock.patch.object(POLICY.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, 'wrong')), \
                mock.patch.object(POLICY, 'get') as get:
            with self.assertRaisesRegex(RuntimeError, 'checkout differs'):
                POLICY.verify()
            get.assert_not_called()

    def test_api_failure_is_closed_and_does_not_disclose_token(self):
        with mock.patch.dict(os.environ, {'GH_TOKEN': 'sensitive-token'}), \
                mock.patch.object(POLICY.urllib.request, 'urlopen', side_effect=urllib.error.URLError('sensitive-token')):
            with self.assertRaises(RuntimeError) as error:
                POLICY.get('environments/test')
            self.assertNotIn('sensitive-token', str(error.exception))

    def test_prepare_verification_checks_policy_before_any_keychain_access(self):
        spec = importlib.util.spec_from_file_location('signer_policy_test', ROOT / 'scripts/macos/release-signing.py')
        signer = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(signer)
        with mock.patch.dict(os.environ, {}, clear=True), mock.patch.object(signer, 'run') as command:
            with self.assertRaises(RuntimeError):
                signer.prepare(verification=True)
            command.assert_not_called()

    def test_workflow_has_no_inputs_caches_release_or_write_permissions(self):
        self.assertIn('workflow_dispatch:', WORKFLOW)
        self.assertNotIn('inputs:', WORKFLOW)
        self.assertNotIn('PNGR_REVIEWED_MAIN_SHA', WORKFLOW)
        self.assertNotIn('secrets: inherit', WORKFLOW)
        self.assertNotIn('actions/cache@', WORKFLOW)
        self.assertNotIn('download-artifact@', WORKFLOW)
        self.assertNotIn('contents: write', WORKFLOW)
        self.assertNotIn('gh release', WORKFLOW)
        self.assertNotIn('git push', WORKFLOW)
        self.assertIn('environment: macos-signing-verification', WORKFLOW)
        self.assertIn('ref: ${{ github.sha }}', WORKFLOW)
        self.assertIn('persist-credentials: false', WORKFLOW)
        self.assertIn('retention-days: 1', WORKFLOW)
        self.assertIn('path: artifacts/signing-verification/*', WORKFLOW)
        self.assertIn("github.repository == 'ryanonmars/pngr'", WORKFLOW)
        self.assertIn("github.ref == 'refs/heads/main'", WORKFLOW)

    def test_only_prepare_receives_five_secrets_and_cleanup_precedes_upload(self):
        prepare = WORKFLOW.split('      - name: Prepare isolated verification signing credentials\n')[1].split('\n      - name:', 1)[0]
        for secret in ('PNGR_MACOS_CERTIFICATE_P12_BASE64', 'PNGR_MACOS_CERTIFICATE_PASSWORD',
                       'PNGR_APPLE_NOTARY_KEY_P8_BASE64', 'PNGR_APPLE_NOTARY_KEY_ID', 'PNGR_APPLE_NOTARY_ISSUER_ID'):
            expression = '${{ secrets.' + secret + ' }}'
            self.assertEqual(WORKFLOW.count(expression), 1)
            self.assertIn(expression, prepare)
        self.assertIn("if: always() && steps.signing.outcome != 'skipped' && steps.signing.outcome != ''", WORKFLOW)
        self.assertLess(WORKFLOW.index('Remove temporary signing credentials'), WORKFLOW.index('Upload verified distributions'))
        self.assertNotIn('continue-on-error:', WORKFLOW)
        self.assertIn('prepare-verification', prepare)
