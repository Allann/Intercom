# Intercom domain glossary

## Peer

One running instance of the Intercom app on a device that can exchange voice and chat directly with other peers.

Each peer has a stable locally generated identity and a user-configured friendly name. Names such as family members' names are deployment data, never hardcoded product concepts.

## Contact

A person the local user can reach. A contact may own multiple approved peers, allowing communication to be routed to the device where that person is currently active. There are no user accounts or cloud identities.

## Approved peer

A peer whose first-contact request has been explicitly accepted on this app instance. Approval is remembered locally. Discovery alone does not grant permission to open audio, send chat, or trigger an attention request.

## Family

The small trusted group of people and devices using an Intercom deployment, potentially spread across multiple households connected by a private network. The MVP does not introduce cloud accounts or a centrally managed family roster.

## Resident app

An Intercom instance configured to launch when the Windows user signs in and remain available from the Windows notification area after its main window closes.

## Local peer

A peer on the same local network segment. Local peers are discovered automatically and form the first supported communication boundary.

## VPN peer

A peer reachable through a routed private network but not necessarily visible to local-network discovery. VPN peers are added manually by IP address or hostname after local peer communication is complete.

## Conversation

A direct or group exchange between peers. Voice is preferred when available; text chat remains available as the fallback.

## Spoken chat

Text chat rendered aloud on the receiving device using an installed Windows speech-synthesis voice. Spoken chat is optional per receiving peer and does not send synthesized audio across the network.

## Push-to-talk

A momentary voice interaction: holding the configured control transmits microphone audio to the selected peer, and releasing it stops transmission.

## Hands-free session

A latched two-way voice interaction between peers. It remains active until toggled off and exists for fixed-room devices or people who cannot comfortably hold the push-to-talk control.

## Voice floor

The exclusive right to transmit into a group voice conversation. A group has at most one current speaker, while text chat remains concurrent.

## Raise hand

A request to receive the voice floor when it becomes available.

## Interrupt request

An urgent request for the current speaker to transfer the voice floor. It does not mix a second audio stream into the conversation.

## Attention card

A peer-to-peer request with a short purpose, notification chime, large visual, and explicit acknowledgement, subject to the receiving peer's interruption mode. Delivery means the card reached a device; acknowledgement means the recipient responded.

## Interruption mode

A peer's current willingness to receive voice and attention requests. The MVP includes an available mode and a do-not-disturb or in-meeting mode.

## Do-not-disturb

An interruption mode in which incoming voice, hands-free requests, attention chimes, and interrupt requests remain silent and become visual notifications. Text is delivered silently. The sender can see this mode before attempting contact.
