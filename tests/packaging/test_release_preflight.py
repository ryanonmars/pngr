"""Execute the release eligibility step without GitHub or publishing side effects."""
import io
import os
from pathlib import Path
import tempfile
import textwrap
import unittest
from unittest import mock
import urllib.error


ROOT = Path(__file__).resolve().parents[2]


def preflight_code():
    workflow = (ROOT / '.github/workflows/release.yml').read_text()
    step = workflow.split('      - name: Skip an already published tag\n', 1)[1]
    lines = step.split('        run: |\n', 1)[1].splitlines()
    body = []
    for line in lines:
        if line.strip() and not line.startswith('          '):
            break
        body.append(line)
    return compile(textwrap.dedent('\n'.join(body)), 'release-preflight', 'exec')


class ReleasePreflightTests(unittest.TestCase):
    def execute(self, response=None, failure=None):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / 'outputs'
            environment = {'GH_TOKEN': 'test-token', 'GH_REPO': 'ryanonmars/pngr',
                           'PNGR_TAG': 'v0.7.3', 'GITHUB_OUTPUT': str(output)}
            with mock.patch.dict(os.environ, environment):
                with mock.patch('urllib.request.urlopen', return_value=response, side_effect=failure) as request:
                    if failure is not None and not (isinstance(failure, urllib.error.HTTPError) and failure.code == 404):
                        with self.assertRaises(type(failure)):
                            exec(preflight_code(), {})
                        self.assertFalse(output.exists(), 'API failures must not authorize packaging or publishing.')
                        return
                    exec(preflight_code(), {})
                    self.assertEqual(30, request.call_args.kwargs['timeout'])
                    self.assertEqual('https://api.github.com/repos/ryanonmars/pngr/releases/tags/v0.7.3',
                                     request.call_args.args[0].full_url)
                    return output.read_text()

    def test_existing_release_skips_duplicate_builds_and_publication(self):
        self.assertEqual('publish=false\n', self.execute(response=io.BytesIO(b'{"id": 123}')))

    def test_only_missing_release_authorizes_builds(self):
        missing = urllib.error.HTTPError('https://api.github.com', 404, 'Not Found', None, None)
        self.assertEqual('publish=true\n', self.execute(failure=missing))

    def test_authentication_rate_limits_and_server_errors_fail_closed(self):
        for status in (401, 403, 429, 500, 503):
            with self.subTest(status=status):
                self.execute(failure=urllib.error.HTTPError('https://api.github.com', status, 'Failure', None, None))

    def test_offline_or_timeout_does_not_authorize_publication(self):
        self.execute(failure=urllib.error.URLError('offline'))
        self.execute(failure=TimeoutError('request timed out'))


if __name__ == '__main__':
    unittest.main()
