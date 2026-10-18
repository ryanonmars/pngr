#!/usr/bin/env python3
"""Fail closed before manual signing: canonical main, immutable dispatch SHA and approval gate."""
import json
import os
import re
import subprocess
import sys
import urllib.request

REPOSITORY = "ryanonmars/pngr"
ENVIRONMENT = "macos-signing-verification"
WORKFLOW = f"{REPOSITORY}/.github/workflows/verify-macos-signing.yml@refs/heads/main"


def context():
    sha = os.environ.get("GITHUB_SHA", "")
    if (os.environ.get("GITHUB_ACTIONS") != "true"
            or os.environ.get("GITHUB_REPOSITORY") != REPOSITORY
            or os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch"
            or os.environ.get("GITHUB_REF") != "refs/heads/main"
            or os.environ.get("GITHUB_WORKFLOW_REF") != WORKFLOW
            or os.environ.get("PNGR_SIGNING_ENVIRONMENT") != ENVIRONMENT
            or not re.fullmatch(r"[0-9a-f]{40}", sha)):
        raise RuntimeError("Verification requires a canonical main dispatch and the signing environment.")
    return sha


def get(path):
    token = os.environ.get("GH_TOKEN", "")
    if not token:
        raise RuntimeError("Environment-policy verification requires the read-only job token.")
    request = urllib.request.Request(f"https://api.github.com/repos/{REPOSITORY}/{path}", headers={
        "Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)
    except Exception:
        raise RuntimeError("Could not verify GitHub environment protections; signing is disabled.") from None


def validate_protections(environment, branches):
    required = [rule for rule in environment.get("protection_rules", [])
                if rule.get("type") == "required_reviewers" and rule.get("reviewers")]
    policy = environment.get("deployment_branch_policy") or {}
    rules = branches.get("branch_policies", [])
    if (environment.get("name") != ENVIRONMENT or not required
            or policy.get("custom_branch_policies") is not True
            or policy.get("protected_branches") is not False
            or len(rules) != 1 or branches.get("total_count") != 1
            or rules[0].get("name") != "main" or rules[0].get("type") != "branch"
            or environment.get("can_admins_bypass") is True):
        raise RuntimeError("Signing environment must require approval and allow only the main branch, without administrator bypass.")


def verify():
    sha = context()
    head = subprocess.run(["git", "rev-parse", "HEAD"], check=True, capture_output=True, text=True).stdout.strip()
    if head != sha:
        raise RuntimeError("The checkout differs from the dispatched workflow commit.")
    validate_protections(get(f"environments/{ENVIRONMENT}"),
                         get(f"environments/{ENVIRONMENT}/deployment-branch-policies?per_page=100"))
    if get("commits/main").get("sha") != sha:
        raise RuntimeError("Main changed since dispatch; start a new verification run.")
    print(f"Verified main commit {sha} and required signing-environment approval policy.")


if __name__ == "__main__":
    try:
        verify()
    except Exception as error:
        # Only our fixed policy errors are safe to print; never emit API/native responses.
        print(str(error) if isinstance(error, RuntimeError) else "Signing verification policy failed.", file=sys.stderr)
        sys.exit(1)
