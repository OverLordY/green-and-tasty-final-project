const fs = require('fs');
const path = require('path');
const template = JSON.parse(fs.readFileSync(path.join(__dirname, 'owner-api.template.json'), 'utf8'));
template.Description = 'Final Green & Tasty API, guest confirmation and restaurant booking workflow.';
delete template.Resources.Users;
delete template.Resources.Client;
delete template.Outputs.UserPoolId;
delete template.Outputs.AppClientId;
Object.assign(template.Parameters, {
  AuthIssuer: { Type: 'String', AllowedPattern: '^https://cognito-idp\\.[a-z0-9-]+\\.amazonaws\\.com/[A-Za-z0-9_-]+$' },
  AuthClientId: { Type: 'String', AllowedPattern: '^[a-zA-Z0-9]+$' },
  FrontendUrl: { Type: 'String', AllowedPattern: '^https://[a-z0-9.-]+$' },
  EmailSender: { Type: 'String', AllowedPattern: '^[A-Za-z0-9._%+@-]+$' },
  SandboxRecipient: { Type: 'String', AllowedPattern: '^[A-Za-z0-9._%+@-]+$' },
  WaiterSubject: { Type: 'String', AllowedPattern: '^[A-Za-z0-9_-]{1,128}$' },
  WaiterLocationId: { Type: 'String', AllowedPattern: '^[A-Za-z0-9_-]{1,128}$' }
});
const env = template.Resources.Function.Properties.Environment.Variables;
for (const [name, route] of Object.entries({ PublicDishesRoute: 'GET /dishes', PopularDishesRoute: 'GET /dishes/popular', LocationDishesRoute: 'GET /locations/{id}/dishes' })) {
  template.Resources[name] = { Type: 'AWS::ApiGatewayV2::Route', Properties: { ApiId: { Ref: 'Api' }, RouteKey: route, Target: { 'Fn::Sub': 'integrations/${Integration}' }, AuthorizationType: 'NONE' } };
}
env.Authentication__Authority = { Ref: 'AuthIssuer' }; env.Authentication__Audience = { Ref: 'AuthClientId' };
env.Email__Provider = 'SES'; env.Email__Sender = { Ref: 'EmailSender' }; env.Email__FrontendBaseUrl = { Ref: 'FrontendUrl' };
env.Restaurant__Staff__0__Subject = { Ref: 'WaiterSubject' }; env.Restaurant__Staff__0__LocationId = { Ref: 'WaiterLocationId' };
env.Cors__AllowedOrigins__0 = { Ref: 'FrontendUrl' }; delete env.Cors__AllowedOrigins__1;
template.Resources.Api.Properties.CorsConfiguration.AllowOrigins = [{ Ref: 'FrontendUrl' }];
template.Resources.Jwt.Properties.JwtConfiguration = { Issuer: { Ref: 'AuthIssuer' }, Audience: [{ Ref: 'AuthClientId' }] };
const statements = template.Resources.ExecutionRole.Properties.Policies[0].PolicyDocument.Statement;
statements[0].Action.push('dynamodb:UpdateItem');
statements.push({ Effect: 'Allow', Action: ['ses:SendEmail'], Resource: [
  { 'Fn::Sub': 'arn:${AWS::Partition}:ses:${AWS::Region}:${AWS::AccountId}:identity/${EmailSender}' },
  { 'Fn::Sub': 'arn:${AWS::Partition}:ses:${AWS::Region}:${AWS::AccountId}:identity/${SandboxRecipient}' }
], Condition: { StringEquals: { 'ses:FromAddress': { Ref: 'EmailSender' } } } });
template.Resources.ExecutionRole.Metadata['com.aws.cloudformation.Context'].must = 'Scope DynamoDB to the group table, SES to the verified sender, and log writes to this function.';
fs.writeFileSync(path.join(__dirname, 'final-api.template.json'), JSON.stringify(template, null, 2) + '\n');
