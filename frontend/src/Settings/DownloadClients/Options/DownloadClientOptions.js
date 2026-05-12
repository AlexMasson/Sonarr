import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import FieldSet from 'Components/FieldSet';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import translate from 'Utilities/String/translate';

function DownloadClientOptions(props) {
  const {
    advancedSettings,
    isFetching,
    error,
    settings,
    hasSettings,
    onInputChange
  } = props;

  return (
    <div>
      {
        isFetching &&
          <LoadingIndicator />
      }

      {
        !isFetching && error &&
          <Alert kind={kinds.DANGER}>
            {translate('DownloadClientOptionsLoadError')}
          </Alert>
      }

      {
        hasSettings && !isFetching && !error && advancedSettings &&
          <div>
            <FieldSet legend={translate('CompletedDownloadHandling')}>

              <Form>
                <FormGroup
                  advancedSettings={advancedSettings}
                  isAdvanced={true}
                  size={sizes.MEDIUM}
                >
                  <FormLabel>{translate('Enable')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="enableCompletedDownloadHandling"
                    helpText={translate('EnableCompletedDownloadHandlingHelpText')}
                    onChange={onInputChange}
                    {...settings.enableCompletedDownloadHandling}
                  />
                </FormGroup>

                <FormGroup
                  advancedSettings={advancedSettings}
                  isAdvanced={true}
                  size={sizes.MEDIUM}
                >
                  <FormLabel>{translate('AutoRedownloadFailed')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.CHECK}
                    name="autoRedownloadFailed"
                    helpText={translate('AutoRedownloadFailedHelpText')}
                    onChange={onInputChange}
                    {...settings.autoRedownloadFailed}
                  />
                </FormGroup>

                {
                  settings.autoRedownloadFailed.value ?
                    <FormGroup
                      advancedSettings={advancedSettings}
                      isAdvanced={true}
                      size={sizes.MEDIUM}
                    >
                      <FormLabel>{translate('AutoRedownloadFailedFromInteractiveSearch')}</FormLabel>

                      <FormInputGroup
                        type={inputTypes.CHECK}
                        name="autoRedownloadFailedFromInteractiveSearch"
                        helpText={translate('AutoRedownloadFailedFromInteractiveSearchHelpText')}
                        onChange={onInputChange}
                        {...settings.autoRedownloadFailedFromInteractiveSearch}
                      />
                    </FormGroup> :
                    null
                }
              </Form>

              <Alert kind={kinds.INFO}>
                {translate('RemoveDownloadsAlert')}
              </Alert>
            </FieldSet>

            <FieldSet legend={translate('LlmPrioritization')}>
              <Form>
                <FormGroup
                  advancedSettings={advancedSettings}
                  isAdvanced={true}
                  size={sizes.MEDIUM}
                >
                  <FormLabel>{translate('LlmApiUrl')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.TEXT}
                    name="llmApiUrl"
                    placeholder="https://api.openai.com/v1"
                    helpText={translate('LlmApiUrlHelpText')}
                    onChange={onInputChange}
                    {...settings.llmApiUrl}
                  />
                </FormGroup>

                <FormGroup
                  advancedSettings={advancedSettings}
                  isAdvanced={true}
                  size={sizes.MEDIUM}
                >
                  <FormLabel>{translate('LlmApiKey')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.PASSWORD}
                    name="llmApiKey"
                    helpText={translate('LlmApiKeyHelpText')}
                    onChange={onInputChange}
                    {...settings.llmApiKey}
                  />
                </FormGroup>

                <FormGroup
                  advancedSettings={advancedSettings}
                  isAdvanced={true}
                  size={sizes.MEDIUM}
                >
                  <FormLabel>{translate('LlmModel')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.TEXT}
                    name="llmModel"
                    placeholder="gpt-4o"
                    helpText={translate('LlmModelHelpText')}
                    onChange={onInputChange}
                    {...settings.llmModel}
                  />
                </FormGroup>

                <FormGroup
                  advancedSettings={advancedSettings}
                  isAdvanced={true}
                  size={sizes.MEDIUM}
                >
                  <FormLabel>{translate('LlmTimeout')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.NUMBER}
                    name="llmTimeout"
                    min={5}
                    max={300}
                    unit="seconds"
                    helpText={translate('LlmTimeoutHelpText')}
                    onChange={onInputChange}
                    {...settings.llmTimeout}
                  />
                </FormGroup>

                <FormGroup
                  advancedSettings={advancedSettings}
                  isAdvanced={true}
                  size={sizes.MEDIUM}
                >
                  <FormLabel>{translate('LlmMaxTokens')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.NUMBER}
                    name="llmMaxTokens"
                    min={0}
                    max={4096}
                    helpText={translate('LlmMaxTokensHelpText')}
                    onChange={onInputChange}
                    {...settings.llmMaxTokens}
                  />
                </FormGroup>

                <FormGroup
                  advancedSettings={advancedSettings}
                  isAdvanced={true}
                  size={sizes.MEDIUM}
                >
                  <FormLabel>{translate('LlmTemperature')}</FormLabel>

                  <FormInputGroup
                    type={inputTypes.NUMBER}
                    name="llmTemperature"
                    min={0}
                    max={2}
                    helpText={translate('LlmTemperatureHelpText')}
                    onChange={onInputChange}
                    {...settings.llmTemperature}
                  />
                </FormGroup>
              </Form>

              <Alert kind={kinds.INFO}>
                {translate('LlmPrioritizationHelpAlert')}
              </Alert>
            </FieldSet>
          </div>
      }
    </div>
  );
}

DownloadClientOptions.propTypes = {
  advancedSettings: PropTypes.bool.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  settings: PropTypes.object.isRequired,
  hasSettings: PropTypes.bool.isRequired,
  onInputChange: PropTypes.func.isRequired
};

export default DownloadClientOptions;
