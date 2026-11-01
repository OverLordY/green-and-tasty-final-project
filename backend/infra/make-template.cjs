const fs = require('fs');
const path = require('path');
const ref = name => ({Ref:name});
const sub = text => ({'Fn::Sub':text});
const att = (name,attr) => ({'Fn::GetAtt':[name,attr]});
const resource = (Type,Properties,why,must,retain=false) => ({Type,Metadata:{'com.aws.cloudformation.Context':{why,must}},...(retain?{DeletionPolicy:'Retain',UpdateReplacePolicy:'Retain'}:{}),Properties});
const template = {
 AWSTemplateFormatVersion:'2010-09-09',
 Description:'EPAM Marathon workshop 5 Owner pre-order API. Dedicated eu-north-1 development stack; signed Cognito identity, bounded DynamoDB aggregate, no public database.',
 Metadata:{AWSToolsMetrics:{AWSAgentToolkit:'aws-cloudformation@3'}},
 Parameters:{CodeBucket:{Type:'String'},CodeKey:{Type:'String'}},
 Resources:{
  Groups:resource('AWS::DynamoDB::Table',{BillingMode:'PAY_PER_REQUEST',AttributeDefinitions:[{AttributeName:'PK',AttributeType:'S'}],KeySchema:[{AttributeName:'PK',KeyType:'HASH'}],SSESpecification:{SSEEnabled:true},Tags:[{Key:'Project',Value:'EPAM-Marathon5'}]},'Stores groups, order state and exact-key lookups atomically.','Retain data; optimistic version conditions; eu-north-1 only.',true),
  Users:resource('AWS::Cognito::UserPool',{UserPoolName:sub('${AWS::StackName}-users'),UserPoolTier:'LITE',AdminCreateUserConfig:{AllowAdminCreateUserOnly:true},Policies:{PasswordPolicy:{MinimumLength:16,RequireUppercase:true,RequireLowercase:true,RequireNumbers:true,RequireSymbols:true}},AccountRecoverySetting:{RecoveryMechanisms:[{Name:'admin_only',Priority:1}]},UserPoolTags:{Project:'EPAM-Marathon5'}},'Dedicated development identities for real JWT ownership checks.','Admin-created synthetic users; no self sign-up or email sends in workshop5; optional MFA is a development exception.',true),
  Client:resource('AWS::Cognito::UserPoolClient',{UserPoolId:ref('Users'),ClientName:'owner-workshop',GenerateSecret:false,ExplicitAuthFlows:['ALLOW_USER_SRP_AUTH','ALLOW_ADMIN_USER_PASSWORD_AUTH'],EnableTokenRevocation:true,PreventUserExistenceErrors:'ENABLED',IdTokenValidity:15,AccessTokenValidity:15,RefreshTokenValidity:1,TokenValidityUnits:{IdToken:'minutes',AccessToken:'minutes',RefreshToken:'days'}},'Public app client compatible with the supplied frontend ID-token contract.','No client secret; admin password flow only for local synthetic-user tests.'),
  LambdaLogs:resource('AWS::Logs::LogGroup',{LogGroupName:sub('/aws/lambda/${AWS::StackName}-api'),RetentionInDays:7},'Bounded retention of API execution logs.','Do not log tokens, passwords or request payloads.'),
  ApiLogs:resource('AWS::Logs::LogGroup',{RetentionInDays:7},'API Gateway request status and timing only.','No authorization headers or request bodies.'),
  ExecutionRole:resource('AWS::IAM::Role',{AssumeRolePolicyDocument:{Version:'2012-10-17',Statement:[{Effect:'Allow',Principal:{Service:'lambda.amazonaws.com'},Action:'sts:AssumeRole'}]},Policies:[{PolicyName:'OwnerApiStorageAndLogs',PolicyDocument:{Version:'2012-10-17',Statement:[{Effect:'Allow',Action:['dynamodb:GetItem','dynamodb:PutItem','dynamodb:DeleteItem','dynamodb:ConditionCheckItem'],Resource:att('Groups','Arn')},{Effect:'Allow',Action:['logs:CreateLogStream','logs:PutLogEvents'],Resource:sub('arn:${AWS::Partition}:logs:${AWS::Region}:${AWS::AccountId}:log-group:/aws/lambda/${AWS::StackName}-api:*')}]}}]},'Runtime can read/write only its aggregate table and log group.','No administrative policies or wildcard data access.'),
  Function:resource('AWS::Lambda::Function',{FunctionName:sub('${AWS::StackName}-api'),Runtime:'dotnet8',Architectures:['arm64'],Handler:'Restaurant.Api',Role:att('ExecutionRole','Arn'),Code:{S3Bucket:ref('CodeBucket'),S3Key:ref('CodeKey')},MemorySize:512,Timeout:15,Environment:{Variables:{ASPNETCORE_ENVIRONMENT:'Production',Authentication__Authority:sub('https://cognito-idp.${AWS::Region}.amazonaws.com/${Users}'),Authentication__Audience:ref('Client'),DynamoDb__TableName:ref('Groups'),Storage__UseInMemory:'false',AllowedHosts:'*',Cors__AllowedOrigins__0:'http://localhost:4200',Cors__AllowedOrigins__1:'http://127.0.0.1:4200'}},Tags:[{Key:'Project',Value:'EPAM-Marathon5'}]},'ASP.NET minimal API hosted on on-demand Lambda.','Production auth and DynamoDB required; no in-memory fallback; compatible ARM64 ZIP.'),
  Api:resource('AWS::ApiGatewayV2::Api',{Name:sub('${AWS::StackName}-http'),ProtocolType:'HTTP',CorsConfiguration:{AllowOrigins:['http://localhost:4200','http://127.0.0.1:4200'],AllowMethods:['GET','POST','DELETE','OPTIONS'],AllowHeaders:['Authorization','Content-Type'],MaxAge:300}},'Low-overhead HTTP API entry point.','Owner routes protected by JWT; explicit localhost CORS.'),
  Integration:resource('AWS::ApiGatewayV2::Integration',{ApiId:ref('Api'),IntegrationType:'AWS_PROXY',IntegrationUri:att('Function','Arn'),PayloadFormatVersion:'2.0',TimeoutInMillis:15000},'Translates HTTP API v2 requests into ASP.NET requests.','Payload format must match LambdaEventSource.HttpApi.'),
  Jwt:resource('AWS::ApiGatewayV2::Authorizer',{ApiId:ref('Api'),AuthorizerType:'JWT',Name:'CognitoOwner',IdentitySource:['$request.header.Authorization'],JwtConfiguration:{Issuer:sub('https://cognito-idp.${AWS::Region}.amazonaws.com/${Users}'),Audience:[ref('Client')]}},'Rejects unsigned/foreign/expired tokens before Lambda.','Application also validates JWT and checks stored OwnerSubject.'),
  DefaultRoute:resource('AWS::ApiGatewayV2::Route',{ApiId:ref('Api'),RouteKey:'$default',Target:sub('integrations/${Integration}'),AuthorizationType:'JWT',AuthorizerId:ref('Jwt')},'Protects all application owner routes by default.','Do not replace JWT authorization with anonymous access.'),
  HealthRoute:resource('AWS::ApiGatewayV2::Route',{ApiId:ref('Api'),RouteKey:'GET /health',Target:sub('integrations/${Integration}'),AuthorizationType:'NONE'},'Minimal public liveness endpoint.','Returns status only.'),
  InviteRoute:resource('AWS::ApiGatewayV2::Route',{ApiId:ref('Api'),RouteKey:'GET /guest/invites/{token}',Target:sub('integrations/${Integration}'),AuthorizationType:'NONE'},'Invitation metadata is accessible to the bearer of its random invitation token.','No guest carts or owner operations exposed anonymously.'),
  OptionsRoute:resource('AWS::ApiGatewayV2::Route',{ApiId:ref('Api'),RouteKey:'OPTIONS /{proxy+}',Target:sub('integrations/${Integration}'),AuthorizationType:'NONE'},'Allows browser CORS preflight with a JWT-protected default route.','No data mutation through OPTIONS.'),
  Stage:resource('AWS::ApiGatewayV2::Stage',{ApiId:ref('Api'),StageName:'$default',AutoDeploy:true,DefaultRouteSettings:{ThrottlingBurstLimit:10,ThrottlingRateLimit:5},AccessLogSettings:{DestinationArn:att('ApiLogs','Arn'),Format:'{"requestId":"$context.requestId","status":"$context.status","route":"$context.routeKey","latency":"$context.responseLatency"}'}},'Automatically publishes workshop routes with conservative throttling.','Record status only; alerts are not a hard spending cap.'),
  InvokePermission:resource('AWS::Lambda::Permission',{FunctionName:ref('Function'),Action:'lambda:InvokeFunction',Principal:'apigateway.amazonaws.com',SourceArn:sub('arn:${AWS::Partition}:execute-api:${AWS::Region}:${AWS::AccountId}:${Api}/*'),SourceAccount:ref('AWS::AccountId')},'Allows only this API Gateway to invoke the function.','Scope permission to this account and API.')
 },
 Outputs:{ApiUrl:{Value:att('Api','ApiEndpoint')},TableName:{Value:ref('Groups')},UserPoolId:{Value:ref('Users')},AppClientId:{Value:ref('Client')},FunctionName:{Value:ref('Function')},Region:{Value:ref('AWS::Region')}}
};
// The default remains JWT-protected. Only guest capability and catalog endpoints bypass
// the gateway authorizer; the application still enforces per-order capabilities and rate limits.
for (const [name, route] of Object.entries({
  GuestCreateRoute: 'POST /guest/orders',
  GuestOrdersReadRoute: 'GET /guest/orders/{proxy+}',
  GuestOrdersWriteRoute: 'POST /guest/orders/{proxy+}',
  LocationsRoute: 'GET /locations',
  LocationDetailRoute: 'GET /locations/{id}',
  AvailableTablesRoute: 'GET /available-tables',
  AvailableSlotsRoute: 'GET /tables/{tableId}/available-time-slots',
})) {
  template.Resources[name] = resource('AWS::ApiGatewayV2::Route', {
    ApiId: ref('Api'), RouteKey: route, Target: sub('integrations/${Integration}'), AuthorizationType: 'NONE'
  }, 'Allows the anonymous guest or catalog flow to reach the API.',
  'Keep anonymous access limited to these routes; guest capabilities and application rate limits remain required.');
}
template.Resources.Function.DependsOn=['LambdaLogs'];
fs.writeFileSync(path.join(__dirname,'owner-api.template.json'),JSON.stringify(template,null,2)+'\n');
console.log('CloudFormation template written.');
