Feature: Verify complex production behavior
  As a person who relies on Intercom
  I want complex production behavior to have executable evidence
  So that changes do not silently break communication between peers

  Background:
    Given the verification run includes all human-authored Intercom.Core production code
    And generated code is excluded from the verification result
    And the OpenCover CRAP threshold is 15

  Scenario: No complex Intercom.Core method is missing deterministic coverage
    When the deterministic verification suite completes
    Then every human-authored Intercom.Core method has an OpenCover CRAP score of 15 or less
    And the same verification run gives the same result when repeated without a product change

  Scenario: A complex Intercom.Core method fails the coverage gate
    Given a human-authored Intercom.Core method has an OpenCover CRAP score greater than 15
    When the coverage gate evaluates the verification result
    Then the coverage gate fails
    And the result identifies each method that is above the threshold
    And generated methods do not appear as failures

  Scenario: Complex resident-app decisions run without a native Windows effect
    Given a complex resident-app behavior depends on Windows or device hardware
    And the native effect is represented by a replaceable boundary
    When the deterministic verification suite exercises the behavior through that boundary
    Then the production decision runs at runtime
    And the verification observes the requested native effect through the boundary
    And the verification does not decide success by reading production source text

  Scenario: A native effect needs Windows or device hardware
    Given a native Windows or device effect cannot be observed deterministically
    When the coverage evidence is reviewed
    Then the production decisions before the native effect have deterministic runtime verification
    And the native effect has an explicit Windows or hardware QA check
    And the QA check states the supported environment and the visible result

  Scenario: Preserve attention-card notification behavior
    Given the current attention-card notification acceptance artifacts are part of the change
    When the complete verification suite runs
    Then the attention-card notification decisions have deterministic runtime verification
    And the attention card remains available in the resident app when Windows suppresses its popup
    And the attention-card notification presentation QA procedure remains available for Windows 10 and Windows 11
