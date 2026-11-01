# Amazon SES production-access request — draft

This draft has not been submitted. Region: `eu-north-1`.

## Proposed account details

- Mail type: transactional.
- Website: https://d1lv4i6uzy4vra.cloudfront.net/
- Contact: the account owner's existing verified sender address.
- Contact language: English.

## Proposed description

Green & Tasty is an educational demonstration of a restaurant group pre-order
application. A booking organizer invites participants to select dishes. A guest
can request a one-time email link to confirm an order or restore access without
creating an account. The system also sends transactional notifications about
organizer changes and operational alerts to the application owner. The project
does not send marketing messages.

The application invalidates old confirmation links when the relevant order
changes, applies a per-order resend cooldown, and provides a generic response to access
recovery requests. Delivery attempts are retried for a limited period. SES
account settings currently list both BOUNCE and COMPLAINT for suppression, and
SES feedback forwarding is enabled for the current sender identity. The owner
must monitor that mailbox and handle feedback, including discontinuing delivery
to affected recipients. Availability and behavior of suppression should be
rechecked after production access is granted.

## Before submitting

- Confirm which sending domain or verified email identity will be used. A
  project-owned domain with DNS-based authentication is recommended by AWS.
- Confirm the owner will monitor the feedback mailbox and operate the process
  for bounce and complaint reports. Feedback forwarding is enabled but a
  complete operational process has not been verified.
- Review whether the guest email flow satisfies the AWS acknowledgement about
  recipient requests and prevent misuse of addresses entered by third parties.
  The current IP rate limiter is process-local and is not a shared rate limit
  across independently scaled Lambda instances. The owner has not yet verified
  that the flow guarantees that recipients explicitly requested every email.
- Review the actual AWS request fields and agree to their acknowledgement only
  when accurate. Do not claim that live restaurant integration exists.
- Submit only after the requested account changes and statements have been
  reviewed; approval is controlled by AWS. This draft is not an approval.
