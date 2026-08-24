Feature: Present an attention card while the resident app is minimized
  As a person who uses a resident app
  I want an incoming attention card to notify me and remain available
  So that I can respond even when the main window is closed

  Background:
    Given the receiving peer runs on a supported version of Windows
    And the receiving peer is an approved peer of the sending peer
    And the receiving peer is in available interruption mode
    And the resident app is available from the Windows notification area
    And the main window is closed

  Scenario Outline: Show an app notification for an incoming attention card
    Given Windows permits Intercom notification banners
    And the receiving peer runs on <Windows version>
    When the sending peer sends an attention card
    Then the receiving peer shows an app notification popup for the attention card
    And the attention card is available in the resident app for acknowledgement
    And diagnostics show that the app notification was submitted to Windows

    Examples:
      | Windows version |
      | Windows 10      |
      | Windows 11      |

  Scenario Outline: Keep the attention card when Windows suppresses its banner
    Given the receiving peer runs on <Windows version>
    And <Windows control> suppresses Intercom notification banners
    When the sending peer sends an attention card
    Then no app notification popup is required
    And the attention card is available in the resident app for acknowledgement
    And diagnostics show that the app notification was submitted to Windows

  Scenario Outline: Report when Windows notification settings block submission
    Given the receiving peer runs on <Windows version>
    And Windows notification settings block Intercom app notifications
    When the sending peer sends an attention card
    Then no app notification popup is required
    And the attention card is available in the resident app for acknowledgement
    And diagnostics show the Windows notification setting that blocked submission

    Examples:
      | Windows version |
      | Windows 10      |
      | Windows 11      |

    Examples:
      | Windows version | Windows control              |
      | Windows 10      | Focus Assist                 |
      | Windows 10      | Windows notification policy |
      | Windows 11      | Do Not Disturb               |
      | Windows 11      | Windows notification policy |

  Scenario Outline: Acknowledge a retained attention card
    Given the receiving peer runs on <Windows version>
    And Windows has suppressed the app notification popup for an attention card
    And the attention card is available in the resident app
    When the recipient acknowledges the attention card
    Then the sending peer can see that the attention card was acknowledged
    And the attention card no longer requires acknowledgement in the resident app

    Examples:
      | Windows version |
      | Windows 10      |
      | Windows 11      |
